using System;
using System.Collections.Generic;
using System.Text;

namespace QlikCloud.IdentityProviders
{
    public partial class Client : RestClientBase
    {
        /// <summary>
        /// Creates a new IdP
        /// </summary>
        /// <remarks>
        /// This endpoint creates an identity provider resource. It returns a 201 Created when creation is successful with a header "QLIK-IDP-POPTS" (A unique string representing a hash of the current configuration being tested), returns a 403 Forbidden for a non TenantAdmin user JWT or if the tenantID in the JWT does not match with any of the tenantIDs in the payload. An IdP can be created with Pending Options or options depending whether the IdP is interactive or not.
        /// </remarks>
        /// <param name="body">Attributes that the user wants to set for a new identity provider resource.</param>
        /// <returns>Created</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual IDP V1IdentityProvidersPost(CreateJWTAuthPayload body = null)
        {
            return System.Threading.Tasks.Task.Run(async () => await V1IdentityProvidersPostAsync(body, System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <summary>
        /// Creates a new IdP
        /// </summary>
        /// <remarks>
        /// This endpoint creates an identity provider resource. It returns a 201 Created when creation is successful with a header "QLIK-IDP-POPTS" (A unique string representing a hash of the current configuration being tested), returns a 403 Forbidden for a non TenantAdmin user JWT or if the tenantID in the JWT does not match with any of the tenantIDs in the payload. An IdP can be created with Pending Options or options depending whether the IdP is interactive or not.
        /// </remarks>
        /// <param name="body">Attributes that the user wants to set for a new identity provider resource.</param>
        /// <returns>Created</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual async System.Threading.Tasks.Task<IDP> V1IdentityProvidersPostAsync(CreateJWTAuthPayload body = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            var disposeClient_ = false;
            try
            {
                using (var request_ = new System.Net.Http.HttpRequestMessage())
                {
                    var json_ = Newtonsoft.Json.JsonConvert.SerializeObject(body, JsonSerializerSettings);
                    var content_ = new System.Net.Http.StringContent(json_);
                    content_.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    request_.Content = content_;
                    request_.Method = new System.Net.Http.HttpMethod("POST");
                    request_.Headers.Accept.Add(System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

                    var urlBuilder_ = new System.Text.StringBuilder();

                    // Operation Path: "v1/identity-providers"
                    urlBuilder_.Append("v1/identity-providers");

                    await PrepareRequestAsync(client_, request_, urlBuilder_, cancellationToken).ConfigureAwait(false);

                    var url_ = urlBuilder_.ToString();
                    request_.RequestUri = new System.Uri(url_, System.UriKind.RelativeOrAbsolute);

                    await PrepareRequestAsync(client_, request_, url_, cancellationToken).ConfigureAwait(false);

                    var response_ = await client_.SendAsync(request_, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
                        if (status_ == 201)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<IDP>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            return objectResponse_.Object;
                        }
                        else
                        if (status_ == 400)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<Errors>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<Errors>("Bad Request", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
                        }
                        else
                        if (status_ == 403)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<Errors>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<Errors>("Forbidden", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
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
        /// <summary>
        /// Creates a new IdP
        /// </summary>
        /// <remarks>
        /// This endpoint creates an identity provider resource. It returns a 201 Created when creation is successful with a header "QLIK-IDP-POPTS" (A unique string representing a hash of the current configuration being tested), returns a 403 Forbidden for a non TenantAdmin user JWT or if the tenantID in the JWT does not match with any of the tenantIDs in the payload. An IdP can be created with Pending Options or options depending whether the IdP is interactive or not.
        /// </remarks>
        /// <param name="body">Attributes that the user wants to set for a new identity provider resource.</param>
        /// <returns>Created</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual IDP V1IdentityProvidersPost(CreateSAMLPayload body = null)
        {
            return System.Threading.Tasks.Task.Run(async () => await V1IdentityProvidersPostAsync(body, System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <summary>
        /// Creates a new IdP
        /// </summary>
        /// <remarks>
        /// This endpoint creates an identity provider resource. It returns a 201 Created when creation is successful with a header "QLIK-IDP-POPTS" (A unique string representing a hash of the current configuration being tested), returns a 403 Forbidden for a non TenantAdmin user JWT or if the tenantID in the JWT does not match with any of the tenantIDs in the payload. An IdP can be created with Pending Options or options depending whether the IdP is interactive or not.
        /// </remarks>
        /// <param name="body">Attributes that the user wants to set for a new identity provider resource.</param>
        /// <returns>Created</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual async System.Threading.Tasks.Task<IDP> V1IdentityProvidersPostAsync(CreateSAMLPayload body = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            var disposeClient_ = false;
            try
            {
                using (var request_ = new System.Net.Http.HttpRequestMessage())
                {
                    var json_ = Newtonsoft.Json.JsonConvert.SerializeObject(body, JsonSerializerSettings);
                    var content_ = new System.Net.Http.StringContent(json_);
                    content_.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    request_.Content = content_;
                    request_.Method = new System.Net.Http.HttpMethod("POST");
                    request_.Headers.Accept.Add(System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

                    var urlBuilder_ = new System.Text.StringBuilder();

                    // Operation Path: "v1/identity-providers"
                    urlBuilder_.Append("v1/identity-providers");

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
                        if (status_ == 201)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<IDP>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            return objectResponse_.Object;
                        }
                        else
                        if (status_ == 400)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<Errors>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<Errors>("Bad Request", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
                        }
                        else
                        if (status_ == 403)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<Errors>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<Errors>("Forbidden", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
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
