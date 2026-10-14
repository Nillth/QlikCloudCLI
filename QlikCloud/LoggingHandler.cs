using System;
using System.Net;
using Serilog;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;
using IdentityModel.Client;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;

namespace QlikCloud
{
    internal class LoggingHandler : DelegatingHandler
    {
        private readonly ILogger _logger;
        public LoggingHandler(ILogger logger, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _logger = logger;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            HttpResponseMessage finalResponse = default;
            do
            {
                _logger.Debug("Request: {Method}: {request}",request.Method, request.RequestUri.AbsoluteUri);
                response?.Dispose(); // Dispose the previous response
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _logger.Debug("Response: {response}", response.StatusCode);
                if (IsRedirect(response.StatusCode))
                {
                    var uri = response.Headers.Location;
                    request.RequestUri = uri; // Update the request URI with the redirect URI
                    finalResponse = response; // Save the last response
                }
                else
                {
                    finalResponse = response; // Save the last response if it's not a redirect
                }
            } while (IsRedirect(response.StatusCode)); // Loop until no more redirection
            return finalResponse; // Return the final response
        }
        private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.MovedPermanently ||
                   statusCode == System.Net.HttpStatusCode.Found ||
                   statusCode == System.Net.HttpStatusCode.SeeOther ||
                   statusCode == System.Net.HttpStatusCode.TemporaryRedirect;
        }
    }
    public class SensitiveDataDestructuringPolicy : IDestructuringPolicy
    {
        private readonly HashSet<string> _sensitiveProperties;
        private readonly HashSet<string> _excludedProperties;

        public SensitiveDataDestructuringPolicy(IEnumerable<string> sensitiveProperties, IEnumerable<string> excludedProperties = default)
        {
            _sensitiveProperties = new HashSet<string>(sensitiveProperties, StringComparer.OrdinalIgnoreCase);
            _excludedProperties = new HashSet<string>(excludedProperties, StringComparer.OrdinalIgnoreCase);
        }

        public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
        {
            result = null;

            var type = value.GetType();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Allow Serilog to handle primitives, strings, and objects without properties
            if (!properties.Any() || type.IsPrimitive || value is string)
            {
                return false; // Defer to Serilog's default destructuring
            }

            // Check if the value is a collection (array, list, etc.)
            if (value is IEnumerable enumerable)
            {
                var modified = false;
                var modifiedElements = new List<LogEventPropertyValue>();

                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        modifiedElements.Add(new ScalarValue(null));
                        continue;
                    }

                    // Check if the item itself is a complex object with properties
                    var itemType = item.GetType();
                    var itemProperties = itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                    if (itemProperties.Any())
                    {
                        var logEventItemProperties = new List<LogEventProperty>();
                        var isSensitive = false;

                        foreach (var itemProp in itemProperties)
                        {
                            object itemPropValue = null;
                            try
                            {
                                itemPropValue = itemProp.GetValue(item);
                            }
                            catch
                            {
                                continue; // Skip inaccessible properties
                            }

                            var itemPropName = itemProp.Name;

                            // Check for sensitive header keys like "Authorization"
                            if (itemPropName.Equals("Key", StringComparison.OrdinalIgnoreCase) &&
                                itemPropValue is string keyValue &&
                                _sensitiveProperties.Contains(keyValue))
                            {
                                isSensitive = true;
                            }

                            // If the value needs obfuscation due to sensitive key, apply obfuscation
                            if (isSensitive && itemPropName.Equals("Value", StringComparison.OrdinalIgnoreCase) && itemPropValue is IEnumerable valueEnumerable)
                            {
                                modified = true;
                                var obfuscatedValues = valueEnumerable.Cast<object>().Select(v => ObfuscateString(v?.ToString() ?? string.Empty));
                                logEventItemProperties.Add(new LogEventProperty(itemPropName, new SequenceValue(obfuscatedValues.Select(v => new ScalarValue(v)))));
                            }
                            else
                            {
                                // Use Serilog's default behavior for non-sensitive properties
                                logEventItemProperties.Add(new LogEventProperty(itemPropName, propertyValueFactory.CreatePropertyValue(itemPropValue, destructureObjects: true)));
                            }
                        }

                        // Add the item as a StructureValue if modified or let Serilog handle it normally
                        if (logEventItemProperties.Any())
                        {
                            modifiedElements.Add(new StructureValue(logEventItemProperties));
                        }
                        else
                        {
                            modifiedElements.Add(propertyValueFactory.CreatePropertyValue(item, destructureObjects: true));
                        }
                    }
                    else
                    {
                        // For items without properties, let Serilog handle them normally
                        modifiedElements.Add(propertyValueFactory.CreatePropertyValue(item, destructureObjects: true));
                    }
                }

                // If any elements were modified, return the modified list; otherwise, let Serilog handle it
                if (modified)
                {
                    result = new SequenceValue(modifiedElements);
                    return true;
                }

                return false; // Defer to Serilog's default destructuring
            }

            // If not an array or collection, process normally for obfuscation and exclusion
            var logEventProperties = new List<LogEventProperty>();

            foreach (var prop in properties)
            {
                object propValue = null;
                try
                {
                    propValue = prop.GetValue(value);
                }
                catch
                {
                    continue; // Skip inaccessible properties
                }

                var propName = prop.Name;

                // Exclude properties marked for exclusion
                if (_excludedProperties.Contains(propName))
                {
                    continue; // Skip this property
                }

                // Obfuscate sensitive properties
                if (_sensitiveProperties.Contains(propName) && propValue is string stringValue)
                {
                    logEventProperties.Add(new LogEventProperty(propName, new ScalarValue(ObfuscateString(stringValue))));
                }
                else
                {
                    logEventProperties.Add(new LogEventProperty(propName, propertyValueFactory.CreatePropertyValue(propValue, destructureObjects: true)));
                }
            }

            result = new StructureValue(logEventProperties);
            return true;
        }

        // Method to obfuscate a string by keeping the first and last characters
        private string ObfuscateString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= 2)
            {
                return value; // No obfuscation if the string is too short
            }
            if (value.StartsWith("Bearer "))
            {
                return "Bearer " + ObfuscateString(value.Substring(7));
            }
            if (value.StartsWith("Basic "))
            {
                return "Basic " + ObfuscateString(value.Substring(6));
            }
            return $"{value.First()}{new string('*', value.Length - 2)}{value.Last()}";
        }
    }
}