using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace QlikCloud.Apps
{
	public partial class Client : RestClientBase
	{

		/// <remarks>
		/// Exports a specific app.
		/// </remarks>
		/// <param name="appId">Identifier of the app.</param>
		/// <param name="file">File To export to.</param>
		/// <param name="noData">The flag indicating if only object contents should be exported.</param>
		/// <returns>Created</returns>
		/// <exception cref="ApiException">A server side error occurred.</exception>
		public virtual APIResponse V1AppsExport(string appId, string file, bool? noData = null)
		{
			return System.Threading.Tasks.Task.Run(async () => await V1AppsExportAsync(appId, new FileInfo(file), noData, System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
		}

		/// <remarks>
		/// Exports a specific app.
		/// </remarks>
		/// <param name="appId">Identifier of the app.</param>
		/// <param name="file">File To export to.</param>
		/// <param name="noData">The flag indicating if only object contents should be exported.</param>
		/// <returns>Created</returns>
		/// <exception cref="ApiException">A server side error occurred.</exception>

		public virtual APIResponse V1AppsExport(string appId, FileInfo file, bool? noData = null)
		{
			return System.Threading.Tasks.Task.Run(async () => await V1AppsExportAsync(appId, file, noData, System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
		}

		/// <remarks>
		/// Exports a specific app.
		/// </remarks>
		/// <param name="appId">Identifier of the app.</param>
		/// <param name="file">File To export to.</param>
		/// <param name="noData">The flag indicating if only object contents should be exported.</param>
		/// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
		/// <returns>Created</returns>
		/// <exception cref="ApiException">A server side error occurred.</exception>

		public virtual async System.Threading.Tasks.Task<APIResponse> V1AppsExportAsync(string appId, string file, bool? noData = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
		{
			return await V1AppsExportAsync(appId, new FileInfo(file), noData, cancellationToken);
		}

		/// <remarks>
		/// Exports a specific app.
		/// </remarks>
		/// <param name="appId">Identifier of the app.</param>
		/// <param name="file">File To export to.</param>
		/// <param name="noData">The flag indicating if only object contents should be exported.</param>
		/// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
		/// <returns>Created</returns>
		/// <exception cref="ApiException">A server side error occurred.</exception>
		public virtual async System.Threading.Tasks.Task<APIResponse> V1AppsExportAsync(string appId, FileInfo file, bool? noData = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
		{
			var response_ = await V1AppsExportAsync(appId, noData, cancellationToken).ConfigureAwait(false);
			try
			{
				var headers_ = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<string>>();
				foreach (var item_ in response_.Headers)
					headers_[item_.Key] = item_.Value;
				var status_ = (int)response_.StatusCode;
				if (status_ == 201)
				{
					// Check if the Location header is present
					if (headers_.TryGetValue("Location", out var locationHeader))
					{
						var downloadUrl = locationHeader.FirstOrDefault();
						if (!string.IsNullOrEmpty(downloadUrl))
						{
							// Download the file from the Location URL
							using (var downloadResponse = await client_.GetAsync(downloadUrl, cancellationToken).ConfigureAwait(false))
							{
								downloadResponse.EnsureSuccessStatusCode();
								using (var fileStream = file.OpenWrite())
								{
									await downloadResponse.Content.CopyToAsync(fileStream).ConfigureAwait(false);
								}
								return new APIResponse<HttpResponseMessage>(status_, headers_, downloadResponse);
							}
						}
					}
				}
				return new APIResponse(status_, headers_);
			}
			catch { }
			return default;
		}
	}
}
