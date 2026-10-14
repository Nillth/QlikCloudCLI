using System;
using System.Globalization;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QlikCloud.Common;

namespace QlikCloudCLI.Runtime
{
    public abstract class PSApiCmdlet : PSConnectionBase
    {
        [Parameter]
        public SwitchParameter RawResponse { get; set; }

        protected void ExecuteApi(Func<CancellationToken, Task<object>> operation, string target, string action, bool modifiesResource)
        {
            if (modifiesResource && !ShouldProcess(target, action)) return;
            RequireConnection();
            object response = null;
            bool written = false;
            try
            {
                response = AwaitOperation(operation(CancellationToken));
                var result = response;
                if (!RawResponse.IsPresent && result is QlikCloud.APIResponse)
                {
                    var payload = result.GetType().GetProperty("Object");
                    if (payload != null) result = payload.GetValue(result);
                }
                if (result != null) WriteObject(result, enumerateCollection: !RawResponse.IsPresent);
                written = true;
            }
            catch (Exception error) { TerminateOperation(error, "QlikCloudOperationFailed", action, target); }
            finally
            {
                // Successful file responses are owned by the caller, not the completed cmdlet.
                if (!written) (response as IDisposable)?.Dispose();
            }
        }
    }

    public abstract class PSModelConstructor<T> : PSCmdlet
    {
        protected bool IsParameterBound(string name) => MyInvocation.BoundParameters.ContainsKey(name);

        private static JsonSerializerSettings ModelSettings() => new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset
        };

        protected T ConvertModelFromInputObject(T input)
        {
            try
            {
                if ((object)input == null) throw new JsonSerializationException();
                // Use the model's SDK JSON attributes/converters and copy nested objects too.
                // Type name metadata is never interpreted during the round trip.
                return ConvertModelFromJson(JsonConvert.SerializeObject(input, ModelSettings()));
            }
            catch (JsonException)
            {
                ThrowTerminatingError(new ErrorRecord(new ArgumentException(
                    "InputObject could not be copied as " + typeof(T).FullName + "."),
                    "QlikCloudInvalidModelInput", ErrorCategory.InvalidData, typeof(T).FullName));
                return default(T);
            }
        }

        protected T ConvertModelFromJson(string json)
        {
            try
            {
                var model = JsonConvert.DeserializeObject<T>(json, ModelSettings());
                if ((object)model == null) throw new JsonSerializationException();
                return model;
            }
            catch (JsonException)
            {
                ThrowTerminatingError(new ErrorRecord(new ArgumentException(
                    "Json must contain a valid " + typeof(T).FullName + " value."),
                    "QlikCloudInvalidModelJson", ErrorCategory.InvalidData, typeof(T).FullName));
                return default(T);
            }
        }
    }
}
