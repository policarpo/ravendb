﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Exceptions.Patching;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.ETL.Metrics;
using Raven.Server.Documents.ETL.Providers.SQL;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.ServerWide.Context;
using Raven.Server.ServerWide.Memory;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Utils;

namespace Raven.Server.Documents.ETL
{
    public abstract class EtlProcess : IDisposable
    {
        public string Tag { get; protected set; }

        public EtlStatistics Statistics { get; protected set; }

        public EtlMetricsCountersManager Metrics { get; protected set; }

        public abstract string Name { get; }

        public abstract void Start();

        public abstract void Stop();

        public abstract void Dispose();

        public abstract void NotifyAboutWork();
    }

    public abstract class EtlProcess<TExtracted, TTransformed> : EtlProcess where TExtracted : ExtractedItem
    {
        private readonly ManualResetEventSlim _waitForChanges = new ManualResetEventSlim();
        private readonly EtlProcessConfiguration _configuration;
        private readonly CancellationTokenSource _cts;
        private Size _currentMaximumAllowedMemory = new Size(32, SizeUnit.Megabytes);
        private NativeMemory.ThreadStats _threadAllocations;
        private Thread _thread;
        protected readonly CurrentEtlRun CurrentBatch = new CurrentEtlRun();
        protected readonly Logger Logger;
        protected readonly DocumentDatabase Database;
        protected TimeSpan? FallbackTime;

        protected EtlProcess(DocumentDatabase database, EtlProcessConfiguration configuration, string tag)
        {
            _configuration = configuration;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(database.DatabaseShutdown);
            Tag = tag;
            Logger = LoggingSource.Instance.GetLogger(database.Name, GetType().FullName);
            Database = database;
            Statistics = new EtlStatistics(tag, _configuration.Name, Database.NotificationCenter);
        }

        protected CancellationToken CancellationToken => _cts.Token;

        public override string Name => _configuration.Name;

        protected abstract IEnumerator<TExtracted> ConvertDocsEnumerator(IEnumerator<Document> docs);

        protected abstract IEnumerator<TExtracted> ConvertTombstonesEnumerator(IEnumerator<DocumentTombstone> tombstones);

        public virtual IEnumerable<TExtracted> Extract(DocumentsOperationContext context)
        {
            var documents = Database.DocumentsStorage.GetDocumentsFrom(context, _configuration.Collection, Statistics.LastProcessedEtag + 1, 0, int.MaxValue);
            var tombstones = Database.DocumentsStorage.GetTombstonesFrom(context, _configuration.Collection, Statistics.LastProcessedEtag + 1, 0, int.MaxValue);

            using (var documentsIt = documents.GetEnumerator())
            using (var tombstonesIt = tombstones.GetEnumerator())
            {
                using (var merged = new MergedEnumerator<TExtracted>())
                {
                    merged.AddEnumerator(ConvertDocsEnumerator(documentsIt));
                    merged.AddEnumerator(ConvertTombstonesEnumerator(tombstonesIt));

                    while (merged.MoveNext())
                    {
                        CurrentBatch.NumberOfExtractedItems++;
                        yield return merged.Current;
                    }
                }
            }
        }

        protected abstract EtlTransformer<TExtracted, TTransformed> GetTransformer(DocumentsOperationContext context);

        public IEnumerable<TTransformed> Transform(IEnumerable<TExtracted> items, DocumentsOperationContext context)
        {
            var transformer = GetTransformer(context);

            foreach (var item in items)
            {
                CancellationToken.ThrowIfCancellationRequested();

                try
                {
                    transformer.Transform(item);

                    Statistics.TransformationSuccess();

                    CurrentBatch.LastTransformedEtag = item.Etag;

                    if (CanContinueBatch() == false)
                        break;
                }
                catch (JavaScriptParseException e)
                {
                    var message = $"[{Name}] Could not parse transformation script. Stopping ETL process.";

                    if (Logger.IsOperationsEnabled)
                        Logger.Operations(message, e);

                    var alert = AlertRaised.Create(
                        Tag,
                        message,
                        AlertType.Etl_TransformationError,
                        NotificationSeverity.Error,
                        key: Name,
                        details: new ExceptionDetails(e));

                    Database.NotificationCenter.Add(alert);

                    Stop();

                    break;
                }
                catch (Exception e)
                {
                    Statistics.RecordTransformationError(e);

                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Could not process SQL ETL script for '{Name}', skipping document: {item.DocumentKey}", e);
                }
            }

            return transformer.GetTransformedResults();
        }

        public void Load(IEnumerable<TTransformed> items, JsonOperationContext context)
        {
            try
            {
                LoadInternal(items, context);

                CurrentBatch.LastLoadedEtag = CurrentBatch.LastTransformedEtag;

                Statistics.LoadSuccess(CurrentBatch.NumberOfExtractedItems);
            }
            catch (Exception e)
            {
                if (Logger.IsOperationsEnabled)
                    Logger.Operations($"Failed to load transformed data for '{Name}'", e);

                HandleFallback();

                Statistics.RecordLoadError(e, CurrentBatch.NumberOfExtractedItems);
            }
        }

        protected virtual void HandleFallback()
        {
        }

        protected abstract void LoadInternal(IEnumerable<TTransformed> items, JsonOperationContext context);

        public bool CanContinueBatch()
        {
            if (CurrentBatch.NumberOfExtractedItems >= Database.Configuration.Etl.MaxNumberOfExtractedDocuments)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Stopping the batch because it has already processed {CurrentBatch.NumberOfExtractedItems} items");

                return false;
            }

            if (CurrentBatch.Duration.Elapsed >= Database.Configuration.Etl.ExtractAndTransformTimeout.AsTimeSpan)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Stopping the batch after {CurrentBatch.Duration.Elapsed} due to extract and transform processing timeout");

                return false;
            }

            if (_threadAllocations.Allocations > _currentMaximumAllowedMemory.GetValue(SizeUnit.Bytes))
            {
                ProcessMemoryUsage memoryUsage;
                if (MemoryUsageGuard.TryIncreasingMemoryUsageForThread(_threadAllocations, ref _currentMaximumAllowedMemory,
                        Database.DocumentsStorage.Environment.Options.RunningOn32Bits, Logger, out memoryUsage) == false)
                {
                    return false;
                }
            }

            return true;
        }

        protected void LoadLastProcessedEtag(DocumentsOperationContext context)
        {
            var doc = Database.DocumentsStorage.Get(context, Constants.Documents.ETL.RavenEtlProcessStatusPrefix + Name);

            if (doc == null)
                Statistics.LastProcessedEtag = 0;
            else
                Statistics.LastProcessedEtag = JsonDeserializationServer.EtlProcessStatus(doc.Data).LastProcessedEtag;
        }

        protected void StoreLastProcessedEtag(DocumentsOperationContext context)
        {
            var key = Constants.Documents.ETL.RavenEtlProcessStatusPrefix + Name;

            var document = context.ReadObject(new DynamicJsonValue
            {
                [nameof(EtlProcessStatus.Name)] = Name,
                [nameof(EtlProcessStatus.LastProcessedEtag)] = Statistics.LastProcessedEtag
            }, key, BlittableJsonDocumentBuilder.UsageMode.ToDisk);

            Database.DocumentsStorage.Put(context, key, null, document);
        }

        protected void UpdateMetrics(DateTime startTime)
        {
            Metrics.BatchSizeMeter.Mark(CurrentBatch.NumberOfExtractedItems);

            Metrics.UpdatePerformanceStats(new EtlPerformanceStats
            {
                BatchSize = CurrentBatch.NumberOfExtractedItems,
                Duration = CurrentBatch.Duration.Elapsed,
                Started = startTime
            });
        }

        public override void NotifyAboutWork()
        {
            _waitForChanges.Set();
        }

        public override void Start()
        {
            if (_thread != null)
                return;

            if (_configuration.Disabled)
                return;

            _thread = new Thread(() =>
            {
                // This has lower priority than request processing, so we let the OS
                // schedule this appropriately
                Threading.TrySettingCurrentThreadPriority(ThreadPriority.BelowNormal);

                Run();
            })
            {
                Name = $"{Tag} process: {Name}",
                IsBackground = true
            };

            if (Logger.IsInfoEnabled)
                Logger.Info($"Starting {Tag} process: '{Name}'.");

            _thread.Start();
        }

        public override void Stop()
        {
            if (_thread == null)
                return;

            if (Logger.IsInfoEnabled)
                Logger.Info($"Stopping {Tag} process: '{Name}'.");

            _cts.Cancel();

            var thread = _thread;
            _thread = null;

            if (Thread.CurrentThread != thread) // prevent a deadlock
                thread.Join();
        }

        public void Run()
        {
            while (CancellationToken.IsCancellationRequested == false)
            {
                _waitForChanges.Reset();

                CurrentBatch.Reset();

                var startTime = Database.Time.GetUtcNow();

                if (FallbackTime != null)
                {
                    Thread.Sleep(FallbackTime.Value);
                    FallbackTime = null;
                }
                
                var didWork = false;

                try
                {
                    EnsureThreadAllocationStats();

                    DocumentsOperationContext context;
                    using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
                    {
                        using (context.OpenReadTransaction())
                        {
                            LoadLastProcessedEtag(context);

                            var extracted = Extract(context);

                            var transformed = Transform(extracted, context);

                            Load(transformed, context);
                            
                            if (CurrentBatch.LastLoadedEtag > Statistics.LastProcessedEtag)
                            {
                                didWork = true;
                                Statistics.LastProcessedEtag = CurrentBatch.LastLoadedEtag;
                            }
                        }

                        if (didWork)
                        {
                            using (var tx = context.OpenWriteTransaction())
                            {
                                StoreLastProcessedEtag(context);
                                tx.Commit();
                            }

                            continue;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    var message = $"Exception in ETL process named '{Name}'";

                    if (Logger.IsInfoEnabled)
                        Logger.Info($"{Tag} {message}", e);
                }
                finally
                {
                    CurrentBatch.Stop();

                    if (didWork)
                    {
                        UpdateMetrics(startTime);

                        if (CancellationToken.IsCancellationRequested == false)
                        {
                            var batchCompleted = Database.EtlLoader.BatchCompleted;
                            batchCompleted?.Invoke(Name, Statistics);
                        }
                    }
                }

                try
                {
                    _waitForChanges.Wait(CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        protected void EnsureThreadAllocationStats()
        {
            _threadAllocations = NativeMemory.ThreadAllocations.Value;
        }

        public override void Dispose()
        {
            if (CancellationToken.IsCancellationRequested)
                return;

            var exceptionAggregator = new ExceptionAggregator(Logger, $"Could not dispose {GetType().Name}: '{Name}'");

            exceptionAggregator.Execute(Stop);
            exceptionAggregator.Execute(() => _cts.Dispose());
            exceptionAggregator.Execute(() => _waitForChanges.Dispose());
            
            exceptionAggregator.ThrowIfNeeded();
        }
    }
}