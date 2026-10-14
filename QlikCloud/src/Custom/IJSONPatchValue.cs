using Newtonsoft.Json.Linq;

namespace QlikCloud.Json
{
    public interface IJSONPatchValue
    {
        JArray ArrayValue { get; set; }
        bool? BoolValue { get; set; }
        string StringValue { get; set; }

        object GetValue();
    }
}