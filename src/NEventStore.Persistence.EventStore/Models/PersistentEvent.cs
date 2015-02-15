﻿using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.ClientAPI;
using NEventStore.Persistence.EventStore.Services;

namespace NEventStore.Persistence.EventStore.Models
{
    public class PersistentEvent
    {
        
        private readonly Dictionary<string, object> _headers;
        private readonly EventMessage _message;
        private readonly string _type;

        public PersistentEvent(EventMessage message, CommitAttempt attempt)
        {
            _message = message;
            _headers = new Dictionary<string, object>();
            _headers = new Dictionary<string, object>
            {
                {Headers.CommitId, attempt.CommitId},
                {Headers.CommitStamp, attempt.CommitStamp},
                {Headers.BucketId, attempt.BucketId},
                {Headers.CommitSequence, attempt.CommitSequence},
                {Headers.StreamRevision, attempt.StreamRevision}
            };
            AddHeaders(message.Headers);
            AddCommitHeaders(attempt);
            _type = message.Body.GetType().FullName;
            
        }

        public Dictionary<string, object> GetCommitHeaders()
        {
            return _headers.Where(kvp => kvp.Key.StartsWith(Headers.CommitHeaders))
                .ToDictionary(kvp => kvp.Key.Substring(Headers.CommitHeaders.Length+1), kvp => kvp.Value);
        }
        public PersistentEvent(ResolvedEvent evt, IEventStoreSerializer serializer)
        {
            _headers =
                (Dictionary<string, object>)
                    serializer.Deserialize(typeof(Dictionary<string, object>).FullName, evt.Event.Metadata);
            _message = new EventMessage
            {
                Body = serializer.Deserialize(evt.Event.EventType, evt.Event.Data),
                Headers = _headers
            };
        }
        public int StreamRevision
        {
            get { return (int)_headers[Headers.StreamRevision]; }
        }
        public int CommitSequence
        {
            get { return (int)_headers[Headers.CommitSequence]; }
        }
        public Guid CommitId
        {
            get { return (Guid)_headers[Headers.CommitId]; }
        }

        public DateTime CommitStamp
        {
            get { return (DateTime)_headers[Headers.CommitStamp]; }
        }

        public string BucketId
        {
            get { return (string)_headers[Headers.BucketId]; }
        }

        private void AddHeaders(Dictionary<string, object> values)
        {
            foreach (var header in values)
            {
                _headers.Add(header.Key, header.Value);
            }
        }

        private void AddCommitHeaders(CommitAttempt attempt)
        {
            foreach (var header in attempt.Headers)
            {
                _headers.Add(string.Format("{0}.{1}", Headers.CommitHeaders, header.Key), header.Value);
            }
        }

   

        public EventMessage ToEventMessage()
        {
            return _message;
        }

        public EventData ToEventData(IEventStoreSerializer serializer)
        {
            return new EventData(Guid.NewGuid(), _type, serializer.IsJsonSerializer, serializer.Serialize(_message.Body),
                serializer.Serialize(_headers));
        }
        
    }
}