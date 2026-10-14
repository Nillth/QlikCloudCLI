using System;
using System.Collections.Generic;
using System.Text;

namespace QlikCloud.Users
{
    public partial class Client : RestClientBase
    {
        /// <summary>
        /// Get my user
        /// </summary>
        /// <remarks>
        /// Redirects to retrieve the user resource associated with the JWT claims.
        /// </remarks>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual User V1UsersMe()
        {
            return System.Threading.Tasks.Task.Run(async () => await V1UsersMeAsync(System.Threading.CancellationToken.None)).GetAwaiter().GetResult().Object;
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <summary>
        /// Get my user
        /// </summary>
        /// <remarks>
        /// Redirects to retrieve the user resource associated with the JWT claims.
        /// </remarks>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual async System.Threading.Tasks.Task<APIResponse<User>> V1UsersMeAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            var disposeClient_ = false;
            try
            {
                using (var request_ = new System.Net.Http.HttpRequestMessage())
                {
                    request_.Method = new System.Net.Http.HttpMethod("GET");

                    var urlBuilder_ = new System.Text.StringBuilder();

                    // Operation Path: "v1/users/me"
                    urlBuilder_.Append("v1/users/me");

                    await PrepareRequestAsync(client_, request_, urlBuilder_, cancellationToken).ConfigureAwait(false);

                    var url_ = urlBuilder_.ToString();
                    request_.RequestUri = new System.Uri(url_, System.UriKind.RelativeOrAbsolute);

                    await PrepareRequestAsync(client_, request_, url_, cancellationToken).ConfigureAwait(false);

                    var response_ = await SendRequestAsync(request_, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    var disposeResponse_ = true;
                    try
                    {
                        var headers_ = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<string>>();
                        foreach (var item_ in response_.Headers)
                            headers_[item_.Key] = item_.Value;
                        if (response_.Content != null && response_.Content.Headers != null)
                        {
                            foreach (var item_ in response_.Content.Headers)
                                headers_[item_.Key] = item_.Value;
                        }

                        await ProcessResponseAsync(client_, response_, cancellationToken).ConfigureAwait(false);

                        var status_ = (int)response_.StatusCode;
                        if (status_ == 301)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<string>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<string>("Successful redirect.", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
                        }
                        else
                        if (status_ == 429)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<Errors>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<Errors>("Request has been rate limited.", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
                        }
                        else

                        if (status_ == 200 || status_ == 204)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<User>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            return new APIResponse<User>(status_, headers_, objectResponse_.Text, objectResponse_.Object);
                        }
                        else
                        {
                            var responseData_ = response_.Content == null ? null : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new ApiException("The HTTP status code of the response was not expected (" + status_ + ").", status_, responseData_, headers_, null);
                        }
                    }
                    finally
                    {
                        if (disposeResponse_)
                            response_.Dispose();
                    }
                }
            }
            finally
            {
                if (disposeClient_)
                    client_.Dispose();
            }
        }
    }
}
