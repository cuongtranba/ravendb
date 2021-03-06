//-----------------------------------------------------------------------
// <copyright file="Documents.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Bundles.Compression.Plugin;
using Raven.Json.Linq;
using Raven.Storage.Esent.StorageActions;

namespace Raven.Database.Storage.Esent.StorageActions
{
    public partial class DocumentStorageActions : IDocumentStorageActions
    {
        public long GetDocumentsCount()
        {
            if (Api.TryMoveFirst(session, Details))
                return Api.RetrieveColumnAsInt32(session, Details, tableColumnsCache.DetailsColumns["document_count"]).Value;
            return 0;
        }

        public JsonDocument DocumentByKey(string key)
        {
            return DocumentByKeyInternal(key, (metadata, createDocument) =>
            {
                System.Diagnostics.Debug.Assert(metadata.Etag != null);
                return new JsonDocument
                {
                    DataAsJson = createDocument(metadata.Key, metadata.Etag, metadata.Metadata),
                    Etag = metadata.Etag,
                    Key = metadata.Key,
                    LastModified = metadata.LastModified,
                    Metadata = metadata.Metadata,
                    NonAuthoritativeInformation = metadata.NonAuthoritativeInformation,
                };
            });
        }

        public Stream RawDocumentByKey(string key)
        {
            Api.JetSetCurrentIndex(session, Documents, "by_key");
            Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
            {
                logger.Debug("Document with key '{0}' was not found", key);
                return null;
            }

            return new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"]));
        }

        public JsonDocumentMetadata DocumentMetadataByKey(string key)
        {
            return DocumentByKeyInternal(key, (metadata, func) => metadata);
        }

        private T DocumentByKeyInternal<T>(string key, Func<JsonDocumentMetadata, Func<string, Etag, RavenJObject, RavenJObject>, T> createResult)
            where T : class
        {

            Api.JetSetCurrentIndex(session, Documents, "by_key");
            Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
            {
                logger.Debug("Document with key '{0}' was not found", key);
                return null;
            }
            var existingEtag = Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]));
            logger.Debug("Document with key '{0}' was found, etag: {1}", key, existingEtag);
            var lastModifiedInt64 = Api.RetrieveColumnAsInt64(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"]).Value;
            return createResult(new JsonDocumentMetadata()
            {
                Etag = existingEtag,
                LastModified = DateTime.FromBinary(lastModifiedInt64),
                Key = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode),
                Metadata = ReadDocumentMetadata(key, existingEtag)
            }, ReadDocumentData);
        }

        private RavenJObject ReadDocumentMetadata(string key, Etag existingEtag)
        {
            try
            {
                var existingCachedDocument = cacher.GetCachedDocument(key, existingEtag);
                if (existingCachedDocument != null)
                    return existingCachedDocument.Metadata;

                return Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]).ToJObject();
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Failed to de-serialize metadata of document " + key, e);
            }
        }

        private RavenJObject ReadDocumentData(string key, Etag existingEtag, RavenJObject metadata)
        {
            try
            {
                var existingCachedDocument = cacher.GetCachedDocument(key, existingEtag);
                if (existingCachedDocument != null)
                    return existingCachedDocument.Document;


                using (Stream stream = new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"])))
                {

                    var size = stream.Length;

                    using (var columnStream = documentCodecs.Aggregate(stream, (dataStream, codec) => codec.Decode(key, metadata, dataStream)))
                    {
                        var data = columnStream.ToJObject();


                        cacher.SetCachedDocument(key, existingEtag, data, metadata, (int) size);

                        return data;
                    }
                }
            }
            catch (Exception e)
            {

                InvalidDataException invalidDataException = null;
                try
                {
                    using (Stream stream = new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"])))
                    using(var reader = new BinaryReader(stream))
                    {
                        if (reader.ReadUInt32() == DocumentCompression.CompressFileMagic)
                        {
                            invalidDataException = new InvalidDataException(string.Format("Document '{0}' is compressed, but the compression bundle is not enabled.\r\n" +
                                                                                          "You have to enable the compression bundle when dealing with compressed documents.", key), e);
                        }
                    }
                }
                catch (Exception )
                {
                    // we are already in error handling mode, just ignore this
                }
                if(invalidDataException != null)
                    throw invalidDataException;

                throw new InvalidDataException("Failed to de-serialize a document: " + key, e);
            }
        }

        public IEnumerable<JsonDocument> GetDocumentsByReverseUpdateOrder(int start, int take)
        {
            Api.JetSetCurrentIndex(session, Documents, "by_etag");
            Api.MoveAfterLast(session, Documents);
            if (TryMoveTableRecords(Documents, start, backward: true))
                return Enumerable.Empty<JsonDocument>();
            if (take < 1024 * 4)
            {
                var optimizer = new OptimizedIndexReader();
                while (Api.TryMovePrevious(session, Documents) && optimizer.Count < take)
                {
                    optimizer.Add(Session, Documents);
                }

                return optimizer.Select(Session, Documents, ReadCurrentDocument);
            }
            return GetDocumentsWithoutBuffering(take);
        }

        private IEnumerable<JsonDocument> GetDocumentsWithoutBuffering(int take)
        {
            while (Api.TryMovePrevious(session, Documents) && take >= 0)
            {
                take--;
                yield return ReadCurrentDocument();
            }
        }

        private bool TryMoveTableRecords(Table table, int start, bool backward)
        {
            if (start <= 0)
                return false;
            if (start == int.MaxValue)
                return true;
            if (backward)
                start *= -1;
            try
            {
                Api.JetMove(session, table, start, MoveGrbit.None);
            }
            catch (EsentErrorException e)
            {
                if (e.Error == JET_err.NoCurrentRecord)
                {
                    return true;
                }
                throw;
            }
            return false;
        }

        private JsonDocument ReadCurrentDocument(string key)
        {
            int docSize;

            var metadataBuffer = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]);
            var metadata = metadataBuffer.ToJObject();

            RavenJObject dataAsJson;
            using (Stream stream = new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"])))
            {
                using (var aggregateStream = documentCodecs.Aggregate(stream, (bytes, codec) => codec.Decode(key, metadata, bytes)))
                {
                    var streamInUse = aggregateStream;
                    if (streamInUse != stream)
                        streamInUse = new CountingStream(aggregateStream);

                    dataAsJson = streamInUse.ToJObject();
                    docSize = (int)Math.Max(streamInUse.Position, stream.Position);
                }
            }

            bool isDocumentModifiedInsideTransaction = false;
            var lastModified = Api.RetrieveColumnAsInt64(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"]).Value;
            return new JsonDocument
            {
                SerializedSizeOnDisk = metadataBuffer.Length + docSize,
                Key = key,
                DataAsJson = dataAsJson,
                NonAuthoritativeInformation = isDocumentModifiedInsideTransaction,
                LastModified = DateTime.FromBinary(lastModified),
                Etag = Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"])),
                Metadata = metadata
            };
        }

        private JsonDocument ReadCurrentDocument()
        {
            var key = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode);
            return ReadCurrentDocument(key);
        }

        public IEnumerable<JsonDocument> GetDocumentsAfterWithIdStartingWith(Etag etag, string idPrefix, int take, CancellationToken cancellationToken, long? maxSize = null, Etag untilEtag = null, TimeSpan? timeout = null, Action<Etag> lastProcessedDocument = null,
            Reference<bool> earlyExit = null)
        {
            if (earlyExit != null)
                earlyExit.Value = false;
            Api.JetSetCurrentIndex(session, Documents, "by_etag");
            Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
                yield break;

            long totalSize = 0;
            int fetchedDocumentCount = 0;

            Stopwatch duration = null;
            if (timeout != null)
                duration = Stopwatch.StartNew();

            Etag lastDocEtag = null;
            Etag docEtag = etag;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                           
                docEtag = Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]));

                // We can skip many documents so the timeout should be at the start of the process to be executed.
                if (timeout != null)
                {
                    if (duration.Elapsed > timeout.Value)
                    {
                        if (earlyExit != null)
                            earlyExit.Value = true;
                        break;
                    }
                }                

                if (untilEtag != null && fetchedDocumentCount > 0)
                {
                    // This is not a failure, we are just ahead of when we expected to. 
                    if (EtagUtil.IsGreaterThan(docEtag, untilEtag))
                        break;
                }

                var key = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode);
                if (!string.IsNullOrEmpty(idPrefix))
                {
                    if (!key.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // We assume that we have processed it because it is not of our interest.
                        lastDocEtag = docEtag;
                        continue;
                    }                        
                }

                var readCurrentDocument = ReadCurrentDocument(key);

                totalSize += readCurrentDocument.SerializedSizeOnDisk;
                fetchedDocumentCount++;

                yield return readCurrentDocument;
                lastDocEtag = docEtag;  

                if (maxSize != null && totalSize > maxSize.Value)
                {
                    if (untilEtag != null && earlyExit != null)
                        earlyExit.Value = true;
                    break;
                }

                if (fetchedDocumentCount >= take)
                {
                    if (untilEtag !=null && earlyExit != null)
                        earlyExit.Value = true;
                    break;
                }
            } 
            while (Api.TryMoveNext(session, Documents));

            // We notify the last that we considered.
            if (lastProcessedDocument != null)
                lastProcessedDocument(lastDocEtag);
        }

        public IEnumerable<JsonDocument> GetDocumentsAfter(Etag etag, int take, CancellationToken cancellationToken, long? maxSize = null, Etag untilEtag = null, TimeSpan? timeout = null, Action<Etag> lastProcessedOnFailure = null,
            Reference<bool> earlyExit = null)
        {
            return GetDocumentsAfterWithIdStartingWith(etag, null, take, cancellationToken, maxSize, untilEtag, timeout, lastProcessedOnFailure, earlyExit);
        }

        public Etag GetBestNextDocumentEtag(Etag etag)
        {
            Api.JetSetCurrentIndex(session, Documents, "by_etag");
            Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
                return etag;


            var val = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"],
                                         RetrieveColumnGrbit.RetrieveFromIndex, null);
            return Etag.Parse(val);
        }

        public DebugDocumentStats GetDocumentStatsVerySlowly()
        {
            var sp = Stopwatch.StartNew();
            var stat = new DebugDocumentStats { Total = GetDocumentsCount() };

            Api.JetSetCurrentIndex(session, Documents, "by_etag");
            Api.MoveBeforeFirst(Session, Documents);
            while (Api.TryMoveNext(Session, Documents))
            {
                var key = Api.RetrieveColumnAsString(Session, Documents, tableColumnsCache.DocumentsColumns["key"],
                                                     Encoding.Unicode);
               
                var metadata = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]).ToJObject();
                var size = Api.RetrieveColumnSize(session, Documents, tableColumnsCache.DocumentsColumns["data"]) ?? -1;
                stat.TotalSize += size;
                if (key.StartsWith("Raven/", StringComparison.OrdinalIgnoreCase))
                {
                    stat.System.Update(size, key);
                }


                var entityName = metadata.Value<string>(Constants.RavenEntityName);
                if (string.IsNullOrEmpty(entityName))
                {
                    stat.NoCollection.Update(size, key);
                }
                else
                {
                    stat.IncrementCollection(entityName, size, key);
                }

                if (metadata.ContainsKey("Raven-Delete-Marker"))
                    stat.Tombstones++;
            }
            stat.TimeToGenerate = sp.Elapsed;
           
            return stat;
        }

        public IEnumerable<JsonDocument> GetDocumentsWithIdStartingWith(string idPrefix, int start, int take, string skipAfter)
        {
            if (take <= 0)
                yield break;
            Api.JetSetCurrentIndex(session, Documents, "by_key");
            Api.MakeKey(session, Documents, skipAfter ?? idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekGE) == false)
                yield break;

            Api.MakeKey(session, Documents, idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey | MakeKeyGrbit.SubStrLimit);
            if (
                Api.TrySetIndexRange(session, Documents, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive) ==
                false)
                yield break;

            if (skipAfter != null && TryMoveTableRecords(Documents, 1, backward: false))
                yield break;

            if (TryMoveTableRecords(Documents, start, backward: false))
                yield break;
            do
            {
                var keyFromDb = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode);
                if (keyFromDb.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) == false)
                {
                    if (StartsWithIgnoreCaseAndSymbols(keyFromDb, idPrefix))
                        continue;
                    yield break;
                }

                yield return ReadCurrentDocument();
                take--;
            } while (Api.TryMoveNext(session, Documents) && take > 0);
        }

        private bool StartsWithIgnoreCaseAndSymbols(string keyFromDb, string idPrefix)
        {
            var keyPos = 0;
            for (int i = 0; i < idPrefix.Length; i++)
            {
                var idChar = idPrefix[i];
                if(char.IsSymbol(idChar) ||
                    char.IsPunctuation(idChar))
                    continue;
                char keyChar;
                do
                {
                    if (keyPos >= keyFromDb.Length)
                        return false; // too short
                    keyChar = keyFromDb[keyPos++];
                } while (char.IsSymbol(keyChar) || char.IsPunctuation(keyChar));
                if(idChar == keyChar)
                    continue;
                // maybe different casing?
                if(char.ToUpperInvariant(keyChar) ==
                    char.ToUpperInvariant(idChar))
                    continue;
                return false;
            }
            return true;
        }

        public Etag GetEtagAfterSkip(Etag etag, int take, CancellationToken cancellationToken)
        {
            Api.JetSetCurrentIndex(session, Documents, "by_etag");
            Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
                return etag;

            if (TryMoveTableRecords(Documents, take, false))
            {
                //skipping failed, will try to move one by one
                Api.JetSetCurrentIndex(session, Documents, "by_etag");
                Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
                if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
                    return etag;

                bool needPrev;
                do
                {
                    needPrev = false;
                    if (take <= 0)
                        break;
                    take--;
                    cancellationToken.ThrowIfCancellationRequested();

                    needPrev = true;
                } while (Api.TryMoveNext(session, Documents));

                if (needPrev)
                {
                    if (Api.TryMovePrevious(session, Documents) == false)
                        return etag;
                }
            }

            return Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]));
        }

        public void TouchDocument(string key, out Etag preTouchEtag, out Etag afterTouchEtag)
        {
            Api.JetSetCurrentIndex(session, Documents, "by_key");
            Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
            var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
            if (isUpdate == false)
            {
                preTouchEtag = null;
                afterTouchEtag = null;
                return;
            }

            preTouchEtag = Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]));
            Etag newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);
            afterTouchEtag = newEtag;
            try
            {
                logger.Debug("Touching document {0} {1} -> {2}", key, preTouchEtag, afterTouchEtag);
                using (var update = new Update(session, Documents, JET_prep.Replace))
                {
                    Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
                    update.Save();
                }
            }
            catch (EsentErrorException e)
            {
                switch (e.Error)
                {
                    case JET_err.WriteConflict:
                    case JET_err.WriteConflictPrimaryIndex:
                        throw new ConcurrencyException("Cannot touch document " + key + " because it is already modified");
                    default:
                        throw;
                }
            }

            etagTouches.Add(preTouchEtag, afterTouchEtag);
        }

        public void IncrementDocumentCount(int value)
        {
            if (Api.TryMoveFirst(session, Details))
                Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], value);
        }

        public AddDocumentResult AddDocument(string key, Etag etag, RavenJObject data, RavenJObject metadata)
        {
            if (key == null) throw new ArgumentNullException("key");
            var byteCount = Encoding.Unicode.GetByteCount(key);
            if (byteCount >= 2048)
                throw new ArgumentException(string.Format("The key must be a maximum of 2,048 bytes in Unicode, 1,024 characters, key is: '{0}'", key), "key");

            try
            {
                Api.JetSetCurrentIndex(session, Documents, "by_key");
                Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
                var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);

                Etag existingEtag = null;
                if (isUpdate)
                {
                    existingEtag = EnsureDocumentEtagMatch(key, etag, "PUT");
                }
                else
                {
                    if (etag != null && etag != Etag.Empty) // expected something to be there.
                        throw new ConcurrencyException("PUT attempted on document '" + key +
                                                       "' using a non current etag (document deleted)")
                        {
                            ExpectedETag = etag
                        };
                    if (Api.TryMoveFirst(session, Details))
                        Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], 1);
                }
                Etag newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);

                DateTime savedAt;
                try
                {
                    using (var update = new Update(session, Documents, isUpdate ? JET_prep.Replace : JET_prep.Insert))
                    {
                        Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["key"], key, Encoding.Unicode);
                        using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"]))
                        {
                            if (isUpdate)
                                columnStream.SetLength(0); // empty the existing value, since we are going to overwrite the entire thing
                            using (Stream stream = new BufferedStream(columnStream))
                            using (
                                var finalStream = documentCodecs.Aggregate(stream, (current, codec) => codec.Encode(key, data, metadata, current))
                                )
                            {
                                data.WriteTo(finalStream);
                                finalStream.Flush();
                            }
                        }
                        Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"],
                                      newEtag.TransformToValueForEsentSorting());

                        savedAt = SystemTime.UtcNow;
                        Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"], savedAt.ToBinary());

                        using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]))
                        {
                            if (isUpdate)
                                columnStream.SetLength(0);
                            using (Stream stream = new BufferedStream(columnStream))
                            {
                                metadata.WriteTo(stream);
                                stream.Flush();
                            }
                        }


                        update.Save();
                    }
                }
                catch (EsentErrorException e)
                {
                    if (e.Error == JET_err.KeyDuplicate || e.Error == JET_err.WriteConflict)
                        throw new ConcurrencyException("PUT attempted on document '" + key + "' concurrently", e);
                    throw;
                }


                logger.Debug("Inserted a new document with key '{0}', update: {1}, ",
                               key, isUpdate);

                cacher.RemoveCachedDocument(key, newEtag);
                return new AddDocumentResult
                {
                    Etag = newEtag,
                    PrevEtag = existingEtag,
                    SavedAt = savedAt,
                    Updated = isUpdate
                };
            }
            catch (EsentKeyDuplicateException e)
            {
                throw new ConcurrencyException("Illegal duplicate key " + key, e);
            }
        }


        public AddDocumentResult InsertDocument(string key, RavenJObject data, RavenJObject metadata, bool overwriteExisting)
        {
            var prep = JET_prep.Insert;
            bool isUpdate = false;

            Etag existingETag = null;
            if (overwriteExisting)
            {
                Api.JetSetCurrentIndex(session, Documents, "by_key");
                Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
                isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
                if (isUpdate)
                {
                    existingETag = Etag.Parse(Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]));
                    prep = JET_prep.Replace;
                }
            }

            try 
            {
            using (var update = new Update(session, Documents, prep))
            {
                Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["key"], key, Encoding.Unicode);
                using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"]))
                {
                    if (isUpdate)
                        columnStream.SetLength(0);
                    using (Stream stream = new BufferedStream(columnStream))
                    using (var finalStream = documentCodecs.Aggregate(stream, (current, codec) => codec.Encode(key, data, metadata, current)))
                    {
                        data.WriteTo(finalStream);
                        finalStream.Flush();
                    }
                }
                Etag newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);
                Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
                DateTime savedAt = SystemTime.UtcNow;
                Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"], savedAt.ToBinary());

                using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]))
                {
                    if (isUpdate)
                        columnStream.SetLength(0);
                    using (Stream stream = new BufferedStream(columnStream))
                    {
                        metadata.WriteTo(stream);
                        stream.Flush();
                    }
                }

                update.Save();

                return new AddDocumentResult
                {
                    Etag = newEtag,
                    PrevEtag = existingETag,
                    SavedAt = savedAt,
                    Updated = isUpdate
                };
            }
        }
            catch (EsentKeyDuplicateException e)
            {
                throw new ConcurrencyException("Illegal duplicate key " + key, e);
            }
        }

        public bool DeleteDocument(string key, Etag etag, out RavenJObject metadata, out Etag deletedETag)
        {
            metadata = null;
            Api.JetSetCurrentIndex(session, Documents, "by_key");
            Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
            {
                logger.Debug("Document with key '{0}' was not found, and considered deleted", key);
                deletedETag = null;
                return false;
            }
            if (Api.TryMoveFirst(session, Details))
                Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], -1);

            var existingEtag = EnsureDocumentEtagMatch(key, etag, "DELETE");

            metadata = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]).ToJObject();
            deletedETag = existingEtag;

            Api.JetDelete(session, Documents);
            logger.Debug("Document with key '{0}' was deleted", key);

            cacher.RemoveCachedDocument(key, existingEtag);

            return true;
        }
    }
}
