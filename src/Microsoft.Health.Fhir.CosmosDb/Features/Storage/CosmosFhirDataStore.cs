﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.HardDelete;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.Upsert;
using Microsoft.Health.Fhir.ValueSets;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage
{
    public sealed class CosmosFhirDataStore : IFhirDataStore, IProvideCapability
    {
        private readonly IScoped<IDocumentClient> _documentClientScope;
        private readonly ICosmosDocumentQueryFactory _cosmosDocumentQueryFactory;
        private readonly RetryExceptionPolicyFactory _retryExceptionPolicyFactory;
        private readonly ILogger<CosmosFhirDataStore> _logger;
        private readonly IModelInfoProvider _modelInfoProvider;
        private readonly IMediator _mediator;

        private readonly UpsertWithHistory _upsertWithHistoryProc;
        private readonly HardDelete _hardDelete;

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosFhirDataStore"/> class.
        /// </summary>
        /// <param name="documentClientScope">
        /// A function that returns an <see cref="IDocumentClient"/>.
        /// Note that this is a function so that the lifetime of the instance is not directly controlled by the IoC container.
        /// </param>
        /// <param name="cosmosDataStoreConfiguration">The data store configuration.</param>
        /// <param name="namedCosmosCollectionConfigurationAccessor">The IOptions accessor to get a named version.</param>
        /// <param name="cosmosDocumentQueryFactory">The factory used to create the document query.</param>
        /// <param name="retryExceptionPolicyFactory">The retry exception policy factory.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="modelInfoProvider">The model provider</param>
        /// <param name="mediator">A mediator instance to broadcast Cosmos DB storage events.</param>
        public CosmosFhirDataStore(
            IScoped<IDocumentClient> documentClientScope,
            CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
            IOptionsMonitor<CosmosCollectionConfiguration> namedCosmosCollectionConfigurationAccessor,
            FhirCosmosDocumentQueryFactory cosmosDocumentQueryFactory,
            RetryExceptionPolicyFactory retryExceptionPolicyFactory,
            ILogger<CosmosFhirDataStore> logger,
            IModelInfoProvider modelInfoProvider,
            IMediator mediator)
        {
            EnsureArg.IsNotNull(documentClientScope, nameof(documentClientScope));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(namedCosmosCollectionConfigurationAccessor, nameof(namedCosmosCollectionConfigurationAccessor));
            EnsureArg.IsNotNull(cosmosDocumentQueryFactory, nameof(cosmosDocumentQueryFactory));
            EnsureArg.IsNotNull(retryExceptionPolicyFactory, nameof(retryExceptionPolicyFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));
            EnsureArg.IsNotNull(modelInfoProvider, nameof(modelInfoProvider));
            EnsureArg.IsNotNull(mediator, nameof(mediator));

            _documentClientScope = documentClientScope;
            _cosmosDocumentQueryFactory = cosmosDocumentQueryFactory;
            _retryExceptionPolicyFactory = retryExceptionPolicyFactory;
            _logger = logger;
            _modelInfoProvider = modelInfoProvider;
            _mediator = mediator;

            CosmosCollectionConfiguration collectionConfiguration = namedCosmosCollectionConfigurationAccessor.Get(Constants.CollectionConfigurationName);

            DatabaseId = cosmosDataStoreConfiguration.DatabaseId;
            CollectionId = collectionConfiguration.CollectionId;
            CollectionUri = cosmosDataStoreConfiguration.GetRelativeCollectionUri(collectionConfiguration.CollectionId);

            _upsertWithHistoryProc = new UpsertWithHistory();
            _hardDelete = new HardDelete();
        }

        private string DatabaseId { get; }

        private string CollectionId { get; }

        private Uri CollectionUri { get; }

        public async Task<UpsertOutcome> UpsertAsync(
            ResourceWrapper resource,
            WeakETag weakETag,
            bool allowCreate,
            bool keepHistory,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            var cosmosWrapper = new FhirCosmosResourceWrapper(resource);
            var notification = new CosmosQueryNotification
            {
                Operation = "Upsert",
                ResourceType = cosmosWrapper.ResourceTypeName,
            };

            try
            {
                _logger.LogDebug($"Upserting {resource.ResourceTypeName}/{resource.ResourceId}, ETag: \"{weakETag?.VersionId}\", AllowCreate: {allowCreate}, KeepHistory: {keepHistory}");

                StoredProcedureResponse<UpsertWithHistoryModel> response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _upsertWithHistoryProc.Execute(
                        _documentClientScope.Value,
                        CollectionUri,
                        cosmosWrapper,
                        weakETag?.VersionId,
                        allowCreate,
                        keepHistory,
                        ct),
                    cancellationToken);

                notification.SetLatency();

                notification.StatusCode = response.StatusCode;
                notification.RequestCharge = response.RequestCharge;

                return new UpsertOutcome(response.Response.Wrapper, response.Response.OutcomeType);
            }
            catch (DocumentClientException dce)
            {
                notification.SetLatency();
                notification.StatusCode = dce.StatusCode;
                notification.RequestCharge = dce.RequestCharge;

                switch (dce.GetSubStatusCode())
                {
                    case HttpStatusCode.PreconditionFailed:
                        throw new ResourceConflictException(weakETag);
                    case HttpStatusCode.NotFound:
                        if (cosmosWrapper.IsDeleted)
                        {
                            return null;
                        }

                        if (weakETag != null)
                        {
                            throw new ResourceConflictException(weakETag);
                        }
                        else if (!allowCreate)
                        {
                            throw new MethodNotAllowedException(Core.Resources.ResourceCreationNotAllowed);
                        }

                        break;

                    case HttpStatusCode.ServiceUnavailable:
                        throw new ServiceUnavailableException();
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");

                throw;
            }
            finally
            {
                await _mediator.Publish<CosmosQueryNotification>(notification, cancellationToken);
            }
        }

        public async Task<ResourceWrapper> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            bool isVersionedRead = !string.IsNullOrEmpty(key.VersionId);

            if (isVersionedRead)
            {
                var sqlParameterCollection = new SqlParameterCollection(new[]
                {
                    new SqlParameter("@resourceId", key.Id),
                    new SqlParameter("@version", key.VersionId),
                });

                var sqlQuerySpec = new SqlQuerySpec("select * from root r where r.resourceId = @resourceId and r.version = @version", sqlParameterCollection);

                var result = await ExecuteDocumentQueryAsync<FhirCosmosResourceWrapper>(
                    sqlQuerySpec,
                    new FeedOptions { PartitionKey = new PartitionKey(key.ToPartitionKey()) },
                    "Get",
                    key.ResourceType,
                    cancellationToken);

                return result.FirstOrDefault();
            }

            var notification = new CosmosQueryNotification
            {
                Operation = "Get",
                ResourceType = key.ResourceType,
            };

            try
            {
                var result = await _documentClientScope.Value.ReadDocumentAsync<FhirCosmosResourceWrapper>(
                    UriFactory.CreateDocumentUri(DatabaseId, CollectionId, key.Id),
                    new RequestOptions { PartitionKey = new PartitionKey(key.ToPartitionKey()) },
                    cancellationToken);

                notification.SetLatency();
                notification.RequestCharge = result.RequestCharge;
                notification.StatusCode = result.StatusCode;

                return result;
            }
            catch (DocumentClientException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            finally
            {
                await _mediator.Publish<CosmosQueryNotification>(notification, cancellationToken);
            }
        }

        public async Task HardDeleteAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            var notification = new CosmosQueryNotification
            {
                Operation = "HardDelete",
                ResourceType = key.ResourceType,
            };

            try
            {
                _logger.LogDebug($"Obliterating {key.ResourceType}/{key.Id}");

                StoredProcedureResponse<IList<string>> response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _hardDelete.Execute(
                        _documentClientScope.Value,
                        CollectionUri,
                        key,
                        ct),
                    cancellationToken);

                notification.SetLatency();
                notification.StatusCode = response.StatusCode;
                notification.RequestCharge = response.RequestCharge;

                _logger.LogDebug($"Hard-deleted {response.Response.Count} documents, which consumed {response.RequestCharge} RUs. The list of hard-deleted documents: {string.Join(", ", response.Response)}.");
            }
            catch (DocumentClientException dce)
            {
                notification.SetLatency();
                notification.StatusCode = dce.StatusCode;
                notification.RequestCharge = dce.RequestCharge;

                if (dce.GetSubStatusCode() == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");

                throw;
            }
            finally
            {
                await _mediator.Publish<CosmosQueryNotification>(notification, cancellationToken);
            }
        }

        internal async Task<FeedResponse<T>> ExecuteDocumentQueryAsync<T>(SqlQuerySpec sqlQuerySpec, FeedOptions feedOptions, string operation, string resourceType, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(sqlQuerySpec, nameof(sqlQuerySpec));

            var context = new CosmosQueryContext(CollectionUri, sqlQuerySpec, feedOptions);

            IDocumentQuery<T> documentQuery = _cosmosDocumentQueryFactory.Create<T>(_documentClientScope.Value, context);

            using (documentQuery)
            {
                var notification = new CosmosQueryNotification
                {
                    Operation = operation,
                    ResourceType = resourceType ?? string.Empty,
                };

                try
                {
                    var result = await documentQuery.ExecuteNextAsync<T>(cancellationToken);

                    notification.SetLatency();
                    notification.RequestCharge = result.RequestCharge;
                    return result;
                }
                catch (DocumentClientException dce)
                {
                    notification.SetLatency();
                    notification.RequestCharge = dce.RequestCharge;
                    notification.StatusCode = dce.StatusCode;

                    throw;
                }
                finally
                {
                    await _mediator.Publish<CosmosQueryNotification>(notification, cancellationToken);
                }
            }
        }

        private static string GetValue(HttpStatusCode type)
        {
            return ((int)type).ToString();
        }

        public void Build(IListedCapabilityStatement statement)
        {
            EnsureArg.IsNotNull(statement, nameof(statement));

            foreach (var resource in _modelInfoProvider.GetResourceTypeNames())
            {
                statement.BuildRestResourceComponent(resource, builder =>
                {
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.NoVersion);
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.Versioned);
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.VersionedUpdate);

                    builder.ReadHistory = true;
                    builder.UpdateCreate = true;
                });
            }
        }
    }
}
