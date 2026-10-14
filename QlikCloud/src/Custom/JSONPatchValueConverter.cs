    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;

namespace QlikCloud.Json
{
    internal class JSONPatchValueConverter<T> : JsonConverter where T : IJSONPatchValue, new()
    {
        public override bool CanConvert(System.Type objectType)
        {
           return objectType == typeof(IJSONPatchValue);
        }
        public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer)
        {
            return ReadJson(reader, objectType, existingValue, false, serializer);
        }

        public object ReadJson(JsonReader reader, System.Type objectType, object existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);

            if (token.Type == JTokenType.String)
                return new T { StringValue = token.ToString() };

            if (token.Type == JTokenType.Boolean)
                return new T { BoolValue = token.ToObject<bool>() };

            if (token.Type == JTokenType.Array)
                return new T { ArrayValue = (JArray)token };

            throw new JsonSerializationException($"Unexpected JSON token {token.Type}");
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is T jsonPatchValue)
            {
                object actualValue = jsonPatchValue.GetValue();
                serializer.Serialize(writer, actualValue);
            }
            else
            {
                writer.WriteNull();
            }
        }
    }

}
