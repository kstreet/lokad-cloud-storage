﻿#region Copyright (c) Lokad 2009
// This code is released under the terms of the new BSD licence.
// URL: http://www.lokad.com/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using Lokad.Diagnostics;
using Lokad.Threading;
using Microsoft.WindowsAzure.StorageClient;

namespace Lokad.Cloud.Azure
{
	/// <summary>Provides access to the Blob Storage.</summary>
	/// <remarks>
	/// All the methods of <see cref="BlobStorageProvider"/> are thread-safe.
	/// </remarks>
	public class BlobStorageProvider : IBlobStorageProvider
	{
		readonly CloudBlobClient _blobStorage;
		readonly IBinaryFormatter _formatter;

		readonly ExecutionCounter _countPutBlob;
		readonly ExecutionCounter _countGetBlob;
		readonly ExecutionCounter _countGetBlobIfModified;
		readonly ExecutionCounter _countUpdateIfNotModified;
		readonly ExecutionCounter _countDeleteBlob;

		public BlobStorageProvider(CloudBlobClient blobStorage, IBinaryFormatter formatter)
		{
			_blobStorage = blobStorage;
			_formatter = formatter;

			ExecutionCounters.Default.RegisterRange(new[]
				{
					_countPutBlob = new ExecutionCounter("BlobStorageProvider.PutBlob", 0, 0),
					_countGetBlob = new ExecutionCounter("BlobStorageProvider.GetBlob", 0, 0),
					_countGetBlobIfModified = new ExecutionCounter("BlobStorageProvider.GetBlobIfModified", 0, 0),
					_countUpdateIfNotModified = new ExecutionCounter("BlobStorageProvider.UpdateIfNotModified", 0, 0),
					_countDeleteBlob = new ExecutionCounter("BlobStorageProvider.DeleteBlob", 0, 0),
				});
		}

		public bool CreateContainer(string containerName)
		{
			var container = _blobStorage.GetContainerReference(containerName);
			try
			{
				container.Create();
				return true;
			}
			catch(StorageClientException ex)
			{
				if(ex.ErrorCode == StorageErrorCode.ContainerAlreadyExists
					|| ex.ErrorCode == StorageErrorCode.ResourceAlreadyExists) return false;
				throw;
			}
		}

		public bool DeleteContainer(string containerName)
		{
			var container = _blobStorage.GetContainerReference(containerName);
			try
			{
				container.Delete();
				return true;
			}
			catch(StorageClientException ex)
			{
				if(ex.ErrorCode == StorageErrorCode.ContainerNotFound
					|| ex.ErrorCode == StorageErrorCode.ResourceNotFound) return false;
				throw;
			}
		}

		public void PutBlob<T>(string containerName, string blobName, T item)
		{
			PutBlob(containerName, blobName, item, true);
		}

		public bool PutBlob<T>(string containerName, string blobName, T item, bool overwrite)
		{
			string ignored;
			return PutBlob(containerName, blobName, item, overwrite, out ignored);
		}

		public bool PutBlob<T>(string containerName, string blobName, T item, bool overwrite, out string etag)
		{
			return PutBlob(containerName, blobName, item, typeof(T), overwrite, out etag);
		}

		static Maybe<string> UploadBlobContent(CloudBlob blob, Stream stream, bool overwrite, string expectedEtag)
		{
			BlobRequestOptions options = null;
			if(overwrite) options = new BlobRequestOptions { AccessCondition = AccessCondition.None };
			else
			{
				if(string.IsNullOrEmpty(expectedEtag)) options = new BlobRequestOptions { AccessCondition = AccessCondition.IfNotModifiedSince(DateTime.MinValue) };
				else options = new BlobRequestOptions { AccessCondition = AccessCondition.None };
			}

			stream.Seek(0, SeekOrigin.Begin);
			try
			{
				blob.UploadFromStream(stream, options);
			}
			catch(StorageClientException ex)
			{
				if(ex.ErrorCode == StorageErrorCode.ConditionFailed)
				{
					return Maybe<string>.Empty;
				}

				throw;
			}

			return Maybe.From(blob.Properties.ETag);
		}

		static Maybe<string> UploadBlobContent(CloudBlob blob, Stream stream, bool overwrite)
		{
			return UploadBlobContent(blob, stream, overwrite, null);
		}

		public bool PutBlob(string containerName, string blobName, object item, Type type, bool overwrite, out string etag)
		{
			var timestamp = _countPutBlob.Open();

			using(var stream = new MemoryStream())
			{
				_formatter.Serialize(stream, item);

				etag = null;

				var container = _blobStorage.GetContainerReference(containerName);

				Func<Maybe<string>> doUpload = () =>
				{
					var blob = container.GetBlobReference(blobName);

					// single remote call
					var result = UploadBlobContent(blob, stream, overwrite);

					return result;
				};

				try
				{
					var result = doUpload();
					if (!result.HasValue)
					{
						etag = null;
						return false;
					}

					etag = result.Value;
					_countPutBlob.Close(timestamp);
					return true;
				}
				catch(StorageClientException ex)
				{
					// if the container does not exist, it gets created
					if(ex.ErrorCode == StorageErrorCode.ContainerNotFound)
					{
						// caution: the container might have been freshly deleted
						// (multiple retries are needed in such a situation)
						Maybe<string> tentativeEtag = Maybe<string>.Empty;
						PolicyHelper.SlowInstantiation.Do(() =>
						{
							container.CreateIfNotExist();

							tentativeEtag = doUpload();

						});

						if (!tentativeEtag.HasValue)
						{
							etag = null;
							return false;
						}

						etag = tentativeEtag.Value;
						_countPutBlob.Close(timestamp);
						return true;
					}

					if(ex.ErrorCode == StorageErrorCode.BlobAlreadyExists && !overwrite)
					{
						// See http://social.msdn.microsoft.com/Forums/en-US/windowsazure/thread/fff78a35-3242-4186-8aee-90d27fbfbfd4
						// and http://social.msdn.microsoft.com/Forums/en-US/windowsazure/thread/86b9f184-c329-4c30-928f-2991f31e904b/

						return false;
					}

					var result = doUpload();
					if (!result.HasValue)
					{
						etag = null;
						return false;
					}

					etag = result.Value;
					_countPutBlob.Close(timestamp);
					return true;
				}
			}
		}

		public Maybe<T> GetBlob<T>(string containerName, string blobName)
		{
			string ignoredEtag;
			return GetBlob<T>(containerName, blobName, out ignoredEtag);
		}

		public Maybe<T> GetBlob<T>(string containerName, string blobName, out string etag)
		{
			return GetBlob(containerName, blobName, typeof (T), out etag)
				.Convert(o => (T) o, Maybe<T>.Empty);
		}

		public Maybe<object> GetBlob(string containerName, string blobName, Type type, out string etag)
		{
			var timestamp = _countGetBlob.Open();

			var container = _blobStorage.GetContainerReference(containerName);
			var blob = container.GetBlockBlobReference(blobName);
			 
			using(var stream = new MemoryStream())
			{
				etag = null;

				// no such container, return default
				try
				{
					blob.DownloadToStream(stream);
					etag = blob.Properties.ETag;
				}
				catch(StorageClientException ex)
				{
					if(ex.ErrorCode == StorageErrorCode.ContainerNotFound
						|| ex.ErrorCode == StorageErrorCode.BlobNotFound
						|| ex.ErrorCode == StorageErrorCode.ResourceNotFound)
					{
						return Maybe<object>.Empty;
					}
					throw;
				}

				stream.Seek(0, SeekOrigin.Begin);
				var deserialized = _formatter.Deserialize(stream, type);

				if (deserialized == null)
				{
					return Maybe<object>.Empty;
				}

				_countGetBlob.Close(timestamp);
				return Maybe.From(deserialized);
			}
		}

		public Maybe<T>[] GetBlobRange<T>(string containerName, string[] blobNames, out string[] etags)
		{
			var tempResult = blobNames.SelectInParallel(blobName =>
				{
					string etag;
					var blob = GetBlob<T>(containerName, blobName, out etag);
					return new Tuple<Maybe<T>, string>(blob, etag);
				}, blobNames.Length);

			etags = new string[blobNames.Length];
			var result = new Maybe<T>[blobNames.Length];

			for (int i = 0; i < tempResult.Length; i++)
			{
				result[i] = tempResult[i].Item1;
				etags[i] = tempResult[i].Item2;
			}

			return result;
		}

		public Maybe<T> GetBlobIfModified<T>(string containerName, string blobName, string oldEtag, out string newEtag)
		{
			// 'oldEtag' is null, then behavior always match simple 'GetBlob'.
			if(null == oldEtag)
			{
				return GetBlob<T>(containerName, blobName, out newEtag);
			}

			var timestamp = _countGetBlobIfModified.Open();

			newEtag = null;

			var container = _blobStorage.GetContainerReference(containerName);
			var blob = container.GetBlobReference(blobName);

			try
			{
				var options = new BlobRequestOptions 
				{ 
					AccessCondition = AccessCondition.IfNoneMatch(oldEtag)
				};

				using (var stream = new MemoryStream())
				{
					blob.DownloadToStream(stream, options);
					newEtag = blob.Properties.ETag;

					stream.Seek(0, SeekOrigin.Begin);
					var deserialized = (T)_formatter.Deserialize(stream, typeof(T));
					_countGetBlobIfModified.Close(timestamp);
					return deserialized;
				}
			}
			catch (StorageClientException ex)
			{
				// call fails because blob has been modified (usual case)
				if(ex.ErrorCode == StorageErrorCode.ConditionFailed ||
					// HACK: BUG in StorageClient 1.0 
					// see http://social.msdn.microsoft.com/Forums/en-US/windowsazure/thread/4817cafa-12d8-4979-b6a7-7bda053e6b21
					ex.Message == @"The condition specified using HTTP conditional header(s) is not met.")
				{
					return Maybe<T>.Empty;
				}

				// call fails due to misc problems
				if (ex.ErrorCode == StorageErrorCode.ContainerNotFound
					|| ex.ErrorCode == StorageErrorCode.BlobNotFound
						|| ex.ErrorCode == StorageErrorCode.ResourceNotFound)
				{
					return Maybe<T>.Empty;
				}

				throw;
			}
		}

		public string GetBlobEtag(string containerName, string blobName)
		{
			var container = _blobStorage.GetContainerReference(containerName);

			try
			{
				var blob = container.GetBlockBlobReference(blobName);
				blob.FetchAttributes();
				return blob.Properties.ETag;
			}
			catch (StorageClientException ex)
			{
				if (ex.ErrorCode == StorageErrorCode.ContainerNotFound
					|| ex.ErrorCode == StorageErrorCode.BlobNotFound
					|| ex.ErrorCode == StorageErrorCode.ResourceNotFound)
				{
					return null;
				}
				throw;
			}
		}

		public bool UpdateIfNotModified<T>(string containerName, string blobName, Func<Maybe<T>, T> updater)
		{
			return UpdateIfNotModified<T>(containerName, blobName, x => Result.CreateSuccess(updater(x)));
		}

		public bool UpdateIfNotModified<T>(string containerName, string blobName, Func<Maybe<T>, Result<T>> updater)
		{
			Result<T> ignored;
			return UpdateIfNotModified(containerName, blobName, updater, out ignored);
		}

		public bool UpdateIfNotModified<T>(string containerName, string blobName, Func<Maybe<T>, T> updater, out T result)
		{
			Result<T> rresult;
			var flag = UpdateIfNotModified(containerName, blobName, x => Result.CreateSuccess(updater(x)), out rresult);

			result = rresult.Value;
			return flag;
		}

		public bool UpdateIfNotModified<T>(string containerName, string blobName, Func<Maybe<T>, Result<T>> updater, out Result<T> result)
		{
			var timestamp = _countUpdateIfNotModified.Open();

			var container = _blobStorage.GetContainerReference(containerName);
			CloudBlockBlob blob = null;

			Maybe<T> input;
			string originalEtag = null;
			try
			{
				blob = container.GetBlockBlobReference(blobName);

				using (var rstream = new MemoryStream())
				{
					blob.DownloadToStream(rstream);
					originalEtag = blob.Properties.ETag;

					rstream.Seek(0, SeekOrigin.Begin);
					var blobData = _formatter.Deserialize(rstream, typeof(T));

					input = blobData == null ? Maybe<T>.Empty : (T) blobData;
				}
			}
			catch (StorageClientException ex)
			{
				// creating the container when missing
				if (ex.ErrorCode == StorageErrorCode.ContainerNotFound
					|| ex.ErrorCode == StorageErrorCode.BlobNotFound
					|| ex.ErrorCode == StorageErrorCode.ResourceNotFound)
				{
					input = Maybe<T>.Empty;
					container.CreateIfNotExist();

					// HACK: if the container has just been deleted,
					// creation could be delayed and would need a proper handling.
				}
				else
				{
					throw;
				}
			}

			// updating the item
			result = updater(input);

			if (!result.IsSuccess)
			{
				return false;
			}

			using (var wstream = new MemoryStream())
			{
				_formatter.Serialize(wstream, result.Value);
				wstream.Seek(0, SeekOrigin.Begin);
				var success = UploadBlobContent(blob, wstream, true, originalEtag).HasValue;
				if(success)
				{
					_countUpdateIfNotModified.Close(timestamp);
				}
				return success;
			}

			
		}

		public bool DeleteBlob(string containerName, string blobName)
		{
			var timestamp = _countDeleteBlob.Open();

			var container = _blobStorage.GetContainerReference(containerName);

			try
			{
				var blob = container.GetBlockBlobReference(blobName);
				blob.Delete();
				_countDeleteBlob.Close(timestamp);
				return true;
			}
			catch (StorageClientException ex) // no such container, return false
			{
				if (ex.ErrorCode == StorageErrorCode.ContainerNotFound
					|| ex.ErrorCode == StorageErrorCode.BlobNotFound
					|| ex.ErrorCode == StorageErrorCode.ResourceNotFound)
				{
					return false;
				}
				throw;
			}
		}

		public IEnumerable<string> List(string containerName, string prefix)
		{
			// Enumerated blobs do not have a "name" property,
			// thus the name must be extracted from their URI
			// http://social.msdn.microsoft.com/Forums/en-US/windowsazure/thread/c5e36676-8d07-46cc-b803-72621a0898b0/?prof=required

			if(prefix == null) prefix = "";

			var container = _blobStorage.GetContainerReference(containerName);

			var options = new BlobRequestOptions
				{
					UseFlatBlobListing = true
				};

			var enumerator = container.ListBlobs(options).GetEnumerator();

			while(true)
			{
				try
				{
					if(!enumerator.MoveNext())
					{
						yield break;
					}
				}
				catch(StorageClientException ex)
				{
					// if the container does not exist, empty enumeration
					if(ex.ErrorCode == StorageErrorCode.ContainerNotFound)
					{
						yield break;
					}
					throw;
				}

				var stringUri = enumerator.Current.Uri.ToString();

				// URI is like this
				// http://azure.whatever.com/container/the-name -- or
				// http://azure.whatever.com/container/a-prefix/the-name -- or
				// http://azure.whatever.com/container/a-prefix/another-prefix/the-name -- etc

				// Full name is extracted like this:
				// Find the first two slashes and take everything after them
				// Find the first slash and take everything after it
				// Find the first slash and take everything after it (again)

				stringUri = stringUri.Substring(stringUri.IndexOf("//") + 2);
				var name = stringUri.Substring(stringUri.IndexOf("/") + 1);
				name = name.Substring(name.IndexOf("/") + 1);

				if(name.StartsWith(prefix)) yield return name;
			}
		}
	}
}
