﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Elasticsearch.Net.Connection.RequestState;
using Elasticsearch.Net.ConnectionPool;
using Elasticsearch.Net.Providers;
using Elasticsearch.Net.Serialization;

namespace Elasticsearch.Net.Connection.RequestHandlers
{
	public class RequestHandlerAsync : RequestHandlerBase
	{
		private class ReadResponse<T>
		{
			public byte[] Bytes { get; set; }
			public ElasticsearchResponse<T> Response { get; set; }
			public ElasticsearchServerError Error { get; set; }
		}

		public RequestHandlerAsync(
			IConnectionConfigurationValues settings,
			IConnectionPool connectionPool,
			IConnection connection,
			IElasticsearchSerializer serializer,
			IMemoryStreamProvider memoryStreamProvider,
			ITransportDelegator delegator)
			: base(settings, connection, connectionPool, serializer, memoryStreamProvider, delegator)
		{
		}

		public Task<ElasticsearchResponse<T>> RequestAsync<T>(TransportRequestState<T> requestState, object data = null)
		{
			var bytes = PostData(data);
			requestState.TickSerialization(bytes);

			return this.DoRequestAsync<T>(requestState)
				.ContinueWith(t => this.SetResultOnRequestState(t, requestState))
				.Unwrap();
		}

		private Task<ElasticsearchResponse<T>> SetResultOnRequestState<T>(Task<ElasticsearchResponse<T>> t, TransportRequestState<T> requestState)
		{
			var tcs = new TaskCompletionSource<ElasticsearchResponse<T>>();
			if (t.Exception != null)
			{
				tcs.SetException(t.Exception.Flatten());
				requestState.SetResult(null);
			}
			else
			{
				tcs.SetResult(t.Result);
				requestState.SetResult(t.Result);
			}
			return tcs.Task;
		}

		private Task<ElasticsearchResponse<T>> DoRequestAsync<T>(TransportRequestState<T> requestState)
		{
			//If connectionSettings is configured to sniff periodically, sniff when stale.
			this._delegator.SniffIfInformationIsTooOld(requestState);

			//select the next node to hit and signal wheter the selected node needs a ping
			var uriRequiresPing = this._delegator.SelectNextNode(requestState);
			if (uriRequiresPing)
			{
				return this._delegator.PingAsync(requestState)
					.ContinueWith(t => this.HandlePingResponse(t, requestState))
					.Unwrap();
			}

			return FinishOrRetryRequestAsync(requestState);
		}

		private Task<ElasticsearchResponse<T>> HandlePingResponse<T>(Task<bool> t, TransportRequestState<T> requestState)
		{
			if (t.IsFaulted)
			{
				requestState.SeenExceptions.Add(t.Exception.InnerException);
				return this.RetryRequestAsync(requestState);
			}
			if (t.IsCompleted)
			{
				return this.FinishOrRetryRequestAsync(requestState);
			}
			return null;
		}

		private Task<ElasticsearchResponse<T>> FinishOrRetryRequestAsync<T>(TransportRequestState<T> requestState)
		{
			var rq = requestState.InitiateRequest(RequestType.ElasticsearchCall);
			return CallIntoConnectionAsync(requestState)
				.ContinueWith(t => HandleStreamResponse(t, rq, requestState))
				.Unwrap();
		}

		private Task<ReadResponse<T>> ReturnVoidResponse<T>(ElasticsearchResponse<Stream> streamResponse)
		{
			streamResponse.Response.Close();
			var voidResponse = ElasticsearchResponse.CloneFrom<VoidResponse>(streamResponse, null);
			return this.ReturnCompletedTaskFor(new ReadResponse<T>() { Response = voidResponse as ElasticsearchResponse<T> });
		}

		private Task<ReadResponse<T>> ReturnTypedResponse<T>(
			ElasticsearchResponse<Stream> streamResponse,
			TransportRequestState<T> requestState)
		{
			//read to ms if needed
			Task<Stream> getStream = null;
			var response = new ReadResponse<T>();
			var hasResponse = streamResponse.Response != null && streamResponse.Response.Length > 0;
			var forceRead = this._settings.KeepRawResponse || typeof(T) == typeof(string) || typeof(T) == typeof(byte[]);
			if (hasResponse && forceRead)
			{
				var memoryStream = this._memoryStreamProvider.New();
				getStream = this.Iterate(this.ReadStreamAsync(streamResponse.Response, memoryStream), memoryStream)
					.ContinueWith(streamReadTask =>
					{
						response.Bytes = streamReadTask.Result.ToArray();
						streamResponse.Response.Close();
						streamReadTask.Result.Position = 0;
						return streamReadTask.Result as Stream;
					});
			}
			else getStream = this.ReturnCompletedTaskFor(streamResponse.Response);
			return getStream.ContinueWith(delegate(Task<Stream> gotStream)
			{
				var isValidResponse = IsValidResponse(requestState, streamResponse);
				var typedResponse = ElasticsearchResponse.CloneFrom<T>(streamResponse, default(T));
				if (!isValidResponse)
				{
					response.Error = GetErrorFromStream<T>(gotStream.Result);
					this.ReturnStringOrByteArray(typedResponse, response.Bytes);
					if (gotStream.Result != null) gotStream.Result.Close();
					response.Response = typedResponse;
					return this.ReturnCompletedTaskFor(response);
				}
				if (this.ReturnStringOrByteArray(typedResponse, response.Bytes))
				{
					if (gotStream.Result != null) gotStream.Result.Close();
					response.Response = typedResponse;
					return this.ReturnCompletedTaskFor(response);
				}
				return this._deserializeAsyncToResponse<T>(gotStream.Result, requestState, typedResponse, response);
			}).Unwrap();
		}

		private Task<ElasticsearchResponse<T>> HandleStreamResponse<T>(Task<ElasticsearchResponse<Stream>> t, IRequestTimings rq, TransportRequestState<T> requestState)
		{
			var streamResponse = t.Result;
			//audit the call into connection straight away
			rq.Finish(streamResponse != null && streamResponse.Success, streamResponse == null ? -1 : streamResponse.HttpStatusCode);
			rq.Dispose();

			//figure out the maximum number of retries, this might 
			var maxRetries = this._delegator.GetMaximumRetries(requestState.RequestConfiguration);

			var retried = requestState.Retried;

			//if havent recieved a successful response and we are not yet done processing the stream attempt a retry
			if (t.Status != TaskStatus.RanToCompletion || !this.DoneProcessing(streamResponse, requestState, maxRetries, retried))
				return this.RetryRequestAsync<T>(requestState);

			//if the response never recieved a status code and has a caught exception make sure we throw it
			if (streamResponse.HttpStatusCode.GetValueOrDefault(-1) <= 0 && streamResponse.OriginalException != null)
				throw streamResponse.OriginalException;

			//if the user explicitly wants a stream return the undisposed stream
			if (typeof(Stream).IsAssignableFrom(typeof(T)))
				return this.ReturnResponseAsTask<T>(streamResponse);

			var readResponseTask = (typeof (VoidResponse).IsAssignableFrom(typeof (T)))
				? this.ReturnVoidResponse<T>(streamResponse)
				: this.ReturnTypedResponse(streamResponse, requestState);

			return readResponseTask.ContinueWith(gotTypedResponse =>
			{
				var r = gotTypedResponse.Result;
				if (this._settings.KeepRawResponse) r.Response.ResponseRaw = r.Bytes;
				this.SetErrorDiagnosticsAndPatchSuccess(requestState, r.Error, r.Response, t.Result);
				if (r.Error != null && this._settings.ThrowOnElasticsearchServerExceptions)
					throw new ElasticsearchServerException(r.Error);
				if (r.Response.SuccessOrKnownError)
					this._connectionPool.MarkAlive(requestState.CurrentNode);
				return r.Response;
			});
		}

		private Task<T> ReturnCompletedTaskFor<T>(T result)
		{
			var tcs = new TaskCompletionSource<T>();
			tcs.SetResult(result);
			return tcs.Task;
		}

		private Task<ElasticsearchResponse<T>> ReturnResponseAsTask<T>(IElasticsearchResponse response)
		{
			var tcs = new TaskCompletionSource<ElasticsearchResponse<T>>();
			tcs.SetResult(response as ElasticsearchResponse<T>);
			return tcs.Task;
		}

		private Task<ElasticsearchResponse<T>> RetryRequestAsync<T>(TransportRequestState<T> requestState)
		{
			var maxRetries = this._delegator.GetMaximumRetries(requestState.RequestConfiguration);

			this._connectionPool.MarkDead(requestState.CurrentNode, this._settings.DeadTimeout, this._settings.MaxDeadTimeout);

			this._delegator.SniffOnConnectionFailure(requestState);

			this.ThrowMaxRetryExceptionWhenNeeded(requestState, maxRetries);

			return this.DoRequestAsync<T>(requestState);
		}

		private Task<ElasticsearchResponse<Stream>> CallIntoConnectionAsync<T>(TransportRequestState<T> requestState)
		{
			var uri = requestState.CreatePathOnCurrentNode();
			var postData = requestState.PostData;
			var requestConfiguration = requestState.RequestConfiguration;
			var method = requestState.Method.ToLowerInvariant();
			try
			{
				switch (method)
				{
					case "head": return this._connection.Head(uri, requestConfiguration);
					case "get": return this._connection.Get(uri, requestConfiguration);
					case "post": return this._connection.Post(uri, postData, requestConfiguration);
					case "put": return this._connection.Put(uri, postData, requestConfiguration);
					case "delete":
						return postData == null || postData.Length == 0
							? this._connection.Delete(uri, requestConfiguration)
							: this._connection.Delete(uri, postData, requestConfiguration);
					default:
						throw new Exception("Unknown HTTP method " + requestState.Method);
				}
			}
			catch (Exception e)
			{
				var tcs = new TaskCompletionSource<ElasticsearchResponse<Stream>>();
				tcs.SetException(e);
				return tcs.Task;
			}
		}

		private Task<MemoryStream> Iterate(IEnumerable<Task> asyncIterator, MemoryStream memoryStream)
		{
			var tcs = new TaskCompletionSource<MemoryStream>();
			var enumerator = asyncIterator.GetEnumerator();
			Action<Task> recursiveBody = null;
			recursiveBody = completedTask =>
			{
				if (completedTask != null && completedTask.IsFaulted)
				{
					var exception = completedTask.Exception.InnerException;
					tcs.TrySetException(exception);
					enumerator.Dispose();
				}
				else if (enumerator.MoveNext())
				{
					enumerator.Current.ContinueWith(recursiveBody);
				}
				else
				{
					tcs.SetResult(memoryStream);
					enumerator.Dispose();
				}
			};
			recursiveBody(null);
			return tcs.Task;
		}

		private Task<ReadResponse<T>> _deserializeAsyncToResponse<T>(
			Stream response, 
			ITransportRequestState requestState, 
			ElasticsearchResponse<T> typedResponse, 
			ReadResponse<T> readResponse)
		{
			var tcs = new TaskCompletionSource<ReadResponse<T>>();
			var responseInstantiater = requestState.ResponseCreationOverride;
			var customConverter = responseInstantiater as Func<IElasticsearchResponse, Stream, T>;
			if (customConverter != null)
			{
				using (response)
				{
					var t = customConverter(typedResponse, response);
					typedResponse.Response = t;
					readResponse.Response = typedResponse;
					tcs.SetResult(readResponse);
					return tcs.Task;
				}
			}
			return this._serializer.DeserializeAsync<T>(response)
				.ContinueWith(t =>
				{
					typedResponse.Response = t.Result;
					readResponse.Response = typedResponse;
					return readResponse;
				});
		}

		private IEnumerable<Task<MemoryStream>> ReadStreamAsync(Stream responseStream, MemoryStream memoryStream)
		{
			var buffer = new byte[BUFFER_SIZE];
			try
			{
				while (responseStream != null)
				{
					var read = Task<int>.Factory.FromAsync(responseStream.BeginRead, responseStream.EndRead, buffer, 0, BUFFER_SIZE, null);
					yield return read.ContinueWith(t => memoryStream);
					if (read.Result == 0) break;
					memoryStream.Write(buffer, 0, read.Result);
				}
			}
			finally
			{
				if (responseStream != null)
					responseStream.Dispose();
			}
		}

	}
}
