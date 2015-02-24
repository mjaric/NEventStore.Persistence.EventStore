﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using NEventStore.Logging;
using NEventStore.Persistence.EventStore.Events;
using NEventStore.Persistence.EventStore.Extensions;
using NEventStore.Persistence.EventStore.Models;
using NEventStore.Persistence.EventStore.Services;
using NEventStore.Persistence.EventStore.Services.Control;
using NEventStore.Persistence.EventStore.Services.Naming;

namespace NEventStore.Persistence.EventStore
{
    public class EventStorePersistenceEngine : IPersistStreams
    {
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof(EventStorePersistenceEngine));
        
        private readonly IEventStoreConnection _connection;
        private readonly IEventStoreSerializer _serializer;
        private readonly IStreamNamingStrategy _namingStrategy;
        private readonly EventStorePersistenceOptions _options;
        private readonly IControlStrategy _controlStrategy;
        
        private class VersionRange
        {

            public VersionRange(int minVersion, int maxVersion)
            {
                MinVersion = TranslateVersion(minVersion);
                MaxVersion = TranslateVersion(maxVersion);
                EventCount = MaxVersion - MinVersion + 1;
                
            }
            private int TranslateVersion(int streamVersion)
            {
                if (streamVersion > 0)
                {
                    return streamVersion - 1;
                }
                return 0;
            }
            public int MinVersion { get; private set; }
            public int MaxVersion { get; private set; }
            public int EventCount { get; private set; }
        }
        private bool _disposed;
        
        public EventStorePersistenceEngine(IEventStoreConnection connection, IEventStoreSerializer serializer,IStreamNamingStrategy namingStrategy,EventStorePersistenceOptions options,bool useProjections)
        {
            _connection = connection;
            _serializer = serializer;
            _options = options;
            if (useProjections )
            {
                if (namingStrategy != null)
                {
                    Logger.Warn("Ignoring naming strategy because it's not supported when using projections");
                }
                _namingStrategy = new DefaultNamingStrategy();
                _controlStrategy = new UseProjectionsStrategy(_options);
            }
            else
            {
                _namingStrategy = namingStrategy;
                _controlStrategy = new NoProjectionStrategy(_connection, _options,namingStrategy,serializer);
            }

            
        }
       
        public void Dispose()
        {
            _disposed = true;
        }

        public IEnumerable<ICommit> GetFrom(string bucketId, string streamId, int minRevision, int maxRevision)
        {
            var range = new VersionRange(minRevision, maxRevision);
            StreamEventsSlice slice = _connection.ReadStreamEventsForwardAsync(_namingStrategy.CreateStream(bucketId, streamId),
                range.MinVersion, range.EventCount, true,_options.UserCredentials).Result;
            PersistentEvent[] events = slice.Events.Select(evt =>
                new PersistentEvent(evt, _serializer)).ToArray();
            return
                events.GroupBy(c => c.CommitId)
                    .Select(
                        g =>
                        {
                            PersistentEvent first = g.First();
                            return new Commit(bucketId, streamId, first.StreamRevision, g.Key, first.CommitSequence,
                                first.CommitStamp, string.Empty, first.GetCommitHeaders(),
                                g.Select(e => e.ToEventMessage()))
                                ;
                        });
        }

        public ICommit Commit(CommitAttempt attempt)
        {
            string streamId = attempt.GetStreamName(_namingStrategy);
            EventStoreTransaction transaction=null;
            try
            {
                
                _controlStrategy.PreProcessCommitAttempt(attempt);
                
                var eventsToSave =
                       attempt.Events.Select(evt => new PersistentEvent(evt, attempt).ToEventData(_serializer)).ToArray();

                //The reason to write the events directly and not the commits is to maintain the event type intact in the event store
                //This can facilitate the writing of projections and listeners do not need to know anything about neventstore
                //also, if we don't store the events it would be much more difficult to recover the events from a revision number
                transaction = _connection.StartTransactionAsync(streamId, attempt.ExpectedVersion(), _options.UserCredentials).Result;
                
                var position = 0;
                while (position < eventsToSave.Length)
                {
                    var pageEvents = eventsToSave.Skip(position).Take(_options.WritePageSize);
                    transaction.WriteAsync(pageEvents).Wait();
                    position += _options.WritePageSize;
                }
                var result = transaction.CommitAsync().Result;
                _controlStrategy.PostProcessCommitAttempt(attempt);
                return attempt.ToCommit(result);
            }
            catch (AggregateException ex)
            {
                if (transaction != null)
                {
                    try
                    {
                        
                        transaction.Rollback();
                    }
                    catch (Exception)
                    {
                        //if the error happens inside the Commit, the transaction is aborted!??
                    }
                    
                }
                foreach (var exception in ex.InnerExceptions   )
                {
                    if (exception is WrongExpectedVersionException)
                    {
                        throw new ConcurrencyException(exception.Message,exception);
                    }
                }
                LogFactory.BuildLogger(GetType()).Error(ex.ToString());
                throw;
            }
            
        }

       

       
        public ISnapshot GetSnapshot(string bucketId, string streamId, int maxRevision)
        {
            StreamEventsSlice currentSlice;
            var nextSliceStart = StreamPosition.End;
            do
            {
                currentSlice =
                _connection.ReadStreamEventsBackwardAsync(_namingStrategy.CreateStreamSnapshots(bucketId,streamId), nextSliceStart,
                                                              _options.ReadPageSize,true,_options.UserCredentials)
                                                              .Result;
                foreach (var resolvedEvent in currentSlice.Events)
                {
                    var snapShot = resolvedEvent.Event.ToSnapshot(_serializer);
                    if (snapShot.StreamRevision <= maxRevision)
                        return snapShot;
                }

                
                nextSliceStart = currentSlice.NextEventNumber;
            } while (!currentSlice.IsEndOfStream);
            return null;

        }

        public bool AddSnapshot(ISnapshot snapshot)
        {
            var last = _connection.GetLast<Snapshot>(snapshot.GetStreamName(_namingStrategy), _serializer,
                _options.UserCredentials);
            _connection.AppendToStreamAsync(snapshot.GetStreamName(_namingStrategy), ExpectedVersion.Any, _options.UserCredentials,
                snapshot.ToEventData(_serializer, new SnapshotMetadata(last))).Wait();
            return true;
            
            

        }
        /// <summary>
        /// This is a very slow operation, use with caution
        /// </summary>
        /// <param name="bucketId"></param>
        /// <param name="maxThreshold"></param>
        /// <returns></returns>
        public IEnumerable<IStreamHead> GetStreamsToSnapshot(string bucketId, int maxThreshold)
        {
            
            StreamEventsSlice currentSlice;
            var nextSliceStart = StreamPosition.Start;
            do
            {
                currentSlice =
                    _connection.ReadStreamEventsForwardAsync(_namingStrategy.CreateStreamsToSnapshot(bucketId),
                        nextSliceStart, _options.ReadPageSize, true, _options.UserCredentials).Result;
                
                foreach (var resolvedEvent in currentSlice.Events)
                {
                    var evt = _serializer.Deserialize<SnapshotThresholdReached>(resolvedEvent.Event.Data);
                    var headRevision =
                        _connection.ReadStreamEventsBackwardAsync(_namingStrategy.CreateStream(bucketId, evt.StreamId),
                            StreamPosition.End, 1, true, _options.UserCredentials).Result.LastEventNumber + 1;
                    var lastSnapShots =
                        _connection.ReadStreamEventsBackwardAsync(
                            _namingStrategy.CreateStreamSnapshots(bucketId, evt.StreamId), StreamPosition.End, 1, true, _options.UserCredentials).Result;
                    var snapshotRevision = 0;
                    if (lastSnapShots.LastEventNumber >= 0)
                    {
                        var snapShot = lastSnapShots.Events.FirstOrDefault().Event.ToSnapshot(_serializer);
                        snapshotRevision = snapShot.StreamRevision;
                    }
                    if (headRevision - snapshotRevision >= maxThreshold)
                    {
                        yield return new StreamHead(bucketId, evt.StreamId, headRevision, snapshotRevision);
                    }
                    

                }
                nextSliceStart = currentSlice.NextEventNumber;
            } while (!currentSlice.IsEndOfStream);
            
        }

        public void Initialize()
        {
            _controlStrategy.Initialize();
        }

        public IEnumerable<ICommit> GetFrom(string bucketId, DateTime start)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<ICommit> GetFrom(string checkpointToken = null)
        {
            throw new NotImplementedException();
        }

        public ICheckpoint GetCheckpoint(string checkpointToken = null)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<ICommit> GetFromTo(string bucketId, DateTime start, DateTime end)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<ICommit> GetUndispatchedCommits()
        {
            throw new NotImplementedException();
        }

        public void MarkCommitAsDispatched(ICommit commit)
        {
            throw new NotImplementedException();
        }

        public void Purge()
        {
            _connection.ActOnAll<string>(_namingStrategy.BucketsStream, Purge, _serializer, _options.UserCredentials);
            _connection.DeleteStreamAsync(_namingStrategy.BucketsStream, ExpectedVersion.Any, _options.UserCredentials).Wait();
        }

        public void Purge(string bucketId)
        {
            string streamId = _namingStrategy.CreateBucketStreamsStream(bucketId);
            _connection.ActOnAll<StreamCreated>(streamId,
                evt => DeleteStream(evt.BucketId, evt.StreamId), _serializer, _options.UserCredentials);
            _connection.DeleteStreamAsync(streamId, ExpectedVersion.Any, _options.UserCredentials).Wait();
            _connection.DeleteStreamAsync(_namingStrategy.CreateStreamsToSnapshot(bucketId), ExpectedVersion.Any, _options.UserCredentials).Wait();
        }

        public void Drop()
        {
            Purge();
        }

        public void DeleteStream(string bucketId, string streamId)
        {
            _connection.DeleteStreamAsync(_namingStrategy.CreateStream(bucketId, streamId), ExpectedVersion.Any, _options.UserCredentials).Wait();
            _connection.DeleteStreamAsync(_namingStrategy.CreateStreamCommits(bucketId, streamId), ExpectedVersion.Any, _options.UserCredentials).Wait();
            _connection.DeleteStreamAsync(_namingStrategy.CreateStreamSnapshots(bucketId, streamId), ExpectedVersion.Any, _options.UserCredentials).Wait();
        }

        public bool IsDisposed
        {
            get { return _disposed; }
        }

        

       

       
    }
}