using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using QlikCloud.Json;

namespace QlikCloud.Glossaries
{
    [JsonConverter(typeof(JSONPatchValueConverter<JSONPatchValue>))]
    public partial class JSONPatchValue : IJSONPatchValue
    {
        public string StringValue { get; set; }
        public bool? BoolValue { get; set; }
        public JArray ArrayValue { get; set; } = new JArray();

        public object GetValue()
        {
            return StringValue ?? (object)BoolValue ?? ArrayValue;
        }
    }
}