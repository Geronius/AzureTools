using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Azure.Tools.ArmParser
{
    static class ParseArmSource
    {
        //defaults
        private const string namespacePropertyName = "namespace_name";

        internal static JObject jsonObject;

        internal static string Execute(string text)
        {
            jsonObject = JObject.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Load });


            foreach (JObject resource in jsonObject["resources"])
            {

                //skip if condition not met
                if (!(resource is JObject)) continue;

                switch (resource["type"].Value<string>())
                {
                    case "Microsoft.Logic/workflows":
                        ParseLogicApps(resource);
                        break;

                    case "Microsoft.ServiceBus/namespaces":
                        ParseNamespaces(resource);
                        break;

                    case "Microsoft.ServiceBus/namespaces/topics":
                        ParseTopics(resource);
                        break;

                }

            }

            string output = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented); //JsonConvert.SerializeObject(jsonObject, Formatting.Indented);



            //find replace
            var parseItems = new Dictionary<string, string>
            {
                //Functions
                {"sites_DevBaasFunctions_name", "functionAppName"},
                //GMF
                {"\"password\": \"B@ss4T%st\"", "\"password\": \"[parameters(\'gmf_api_Pass\')]\""},
                {"\"password\": \"7Jt7*R\"", "\"password\": \"[parameters(\'gmf_api_Pass\')]\""},
                {"\"username\": \"baas.stage.api\"", "\"username\": \"[parameters(\'gmf_api_user\')]\""},
                {"\"username\": \"baas.prod.api\"", "\"username\": \"[parameters(\'gmf_api_user\')]\""},
                {"\"PlatformUser\": \"baas.stage.api\"", "\"PlatformUser\": \"[parameters(\'gmf_api_user\')]\""},
                {"\"PlatformUser\": \"baas.prod.api\"", "\"PlatformUser\": \"[parameters(\'gmf_api_user\')]\""},
                //SharePoint
                {
                    "\"relativeWebUrl\": \"sites/DSP-Test\"",
                    "\"relativeWebUrl\": \"[parameters(\'sharepointSiteRelativeWebUrl\')]\""
                },


            };




            foreach (var item in parseItems)
            {
                output = output.Replace(item.Key, item.Value);
            }

            return output;
        }


        private static void ParseNamespaces(JObject resource)
        {

            //get parameter array
            var parameters = jsonObject["parameters"] as JObject;

            //define name
            var name = string.Empty;
            if (resource["name"].Value<string>() == $"[parameters('{namespacePropertyName}')]")
            {
                name = parameters.SelectToken($"$..{namespacePropertyName}..defaultValue").Value<string>();
            }
            else
            {
                name = $"{resource["name"]}".Replace("[parameters('namespaces_", "").Replace("_name')]", "");
            }


            //add namespace_name_parameter is not exist
            var namespace_name_parameter = parameters.SelectToken(namespacePropertyName);
            if (namespace_name_parameter == null)
            {
                parameters.Add(new JProperty(namespacePropertyName, new JObject(new JProperty("defaultValue", name), new JProperty("type", "string"))));
            }

            //change name parameter in fixed value
            resource["name"] = $"[parameters('{namespacePropertyName}')]";

            //change location to location of resourcegroup.
            resource["location"] = "[resourceGroup().location]";

            //change tag to fixed value
            if (resource?["tags"] == null)
            {
                var l = resource["location"].Parent;
                l.AddAfterSelf(new JProperty("tags", new JObject(new JProperty("displayName", name))));
            }
            else
            {
                resource["tags"]["displayName"] = name;
            }


            //set some properties
            foreach (var prop in resource["properties"].Values<JProperty>())
            {
                if (prop.Name == "serviceBusEndpoint")
                {
                    prop.Value = $"[concat('https://', parameters('{namespacePropertyName}'),'.servicebus.windows.net:443/')]";
                }
                else if (prop.Name == "metricId")
                {
                    prop.Value = $"[concat(subscription().subscriptionId, ':', parameters('{namespacePropertyName}'))]";
                }
            }

        }

        private static void ParseTopics(JObject resource)
        {
            //define name
            var nameArray = $"{resource["name"]}".Split('/');
            var name = $"[concat(parameters('{namespacePropertyName}'), '/" + nameArray[1].Replace("parameters('topics_", "'").Replace("_name')", "'");
            var displayName = name.Split('/')[1].Replace("parameters('topics_", "").Replace(@"_name')", "").Replace(@"', '", "").Replace(@"')]", "");

            //get parameter array
            var parameters = jsonObject["parameters"] as JObject;

            //add namespace_name_parameter is not exist
            var namespace_name_parameter = parameters.SelectToken(namespacePropertyName);
            if (namespace_name_parameter == null)
            {
                parameters.Add(new JProperty(namespacePropertyName, new JObject(new JProperty("defaultValue", nameArray[0].Replace("[concat(parameters('namespaces_", "").Replace("_name'), '", "")), new JProperty("type", "string"))));
            }

            //change name parameter in fixed value
            resource["name"] = name;

            //change location to location of resourcegroup.
            resource["location"] = "[resourceGroup().location]";

            //change tag to fixed value
            if (resource?["tags"] == null)
            {
                var l = resource["location"].Parent;
                l.AddAfterSelf(new JProperty("tags", new JObject(new JProperty("displayName", "servicebus_namespace"))));
            }
            else
            {
                resource["tags"]["displayName"] = "servicebus_namespace";
            }

            //set dependencies
            resource["dependsOn"] = new JArray();
            //JArray dependsOn = ((JArray)resource["dependsOn"]);
            //dependsOn.Add(new JValue($"[resourceId('Microsoft.ServiceBus/namespaces', parameters('{namespacePropertyName}'))]"));
            
        }

        private static void ParseLogicApps(JObject resource)
        {

            //define name
            string name = $"{resource["name"]}".Replace("[parameters('workflows_", "").Replace("_name')]", "");


            //define definition
            var definition = (JObject)resource?["properties"]?["definition"];

            //remove dependings of websites, not in this ARM
            if (resource["dependsOn"] != null)
                for (var i = 0; i < ((JArray)resource["dependsOn"]).Count; i++)
                {
                    var dependencie = ((JArray)resource["dependsOn"])[i];
                    if (dependencie.Value<string>().StartsWith("[resourceId('Microsoft.Web/sites'"))
                    {
                        ((JArray)resource["dependsOn"]).Remove(dependencie);
                    }
                }

            //add resources if not add yet
            if (resource["resources"] == null)
            {
                var l = (resource?["properties"]).Parent;
                l.AddAfterSelf(new JProperty("resources", new JArray()));
            }


            //add OMS if not added yet, else replace
            var resources = ((JArray)resource?["resources"]);
            var diagnosticSettings =
                resources.FirstOrDefault(r => r["type"].Value<string>() == "providers/diagnosticSettings");

            if (diagnosticSettings != null)
                resources.Remove(diagnosticSettings);

            diagnosticSettings = new JObject(
                new JProperty("type", "providers/diagnosticSettings"),
                new JProperty("name", $"Microsoft.Insights/service"),
                new JProperty("dependsOn",
                    new JArray($"[resourceId('Microsoft.Logic/workflows', '{name}')]")),
                new JProperty("apiVersion", "2015-07-01"),
                new JProperty("properties",
                    new JObject(
                        new JProperty("workspaceId",
                            "[resourceId('Microsoft.OperationalInsights/workspaces/', parameters('omsWorkspaceName'))]"),
                        new JProperty("logs",
                            new JArray(new JObject(
                                new JProperty("category", "WorkflowRuntime"),
                                new JProperty("enabled", true),
                                new JProperty("retentionPolicy",
                                    new JObject(new JProperty("days", 0), new JProperty("enabled", false))))
                            )
                        ),
                        new JProperty("metrics",
                            new JArray(new JObject(
                                new JProperty("timeGrain", "PT1M"),
                                new JProperty("enabled", true),
                                new JProperty("retentionPolicy",
                                    new JObject(new JProperty("enabled", false),
                                        new JProperty("days", 0)))))
                        )
                    )
                )
            );

            resources.Add(diagnosticSettings);


            //change name parameter in fixed value
            resource["name"] = name;

            //change location to location of resourcegroup.
            resource["location"] = "[resourceGroup().location]";

            //change tag to fixed value
            if (resource?["tags"] == null)
            {
                var l = ((JToken)resource["location"]).Parent;
                l.AddAfterSelf(new JProperty("tags", new JObject(new JProperty("displayName", name))));
            }
            else
            {
                resource["tags"]["displayName"] = name;
            }

            //change trigger interval into parameter
            var recurrence = definition.SelectToken("$.triggers..recurrence");
            if (recurrence?["interval"].Value<string>() == "3")
                recurrence["interval"] = "[parameters('LogicAppTriggerInterVal')]";


            //update uri's
            foreach (var uri in definition.SelectTokens("$..inputs.uri"))
            {
                var defaultValue = (jsonObject["parameters"]).SelectTokens("$..defaultValue")
                    .FirstOrDefault(p => uri.ToString().StartsWith(p.ToString()));
                if (defaultValue != null && !string.IsNullOrWhiteSpace(defaultValue.Value<string>()))
                {
                    var paramName = ((JProperty)defaultValue.Parent.Parent.Parent).Name;
                    var input = uri.Parent.Parent as dynamic;
                    input.uri =
                        $"[concat(parameters('{paramName}'), '{(input.uri.ToString().Replace(defaultValue.ToString(), "")).Replace("'", "', variables('quote'), '")}')]";
                }
            }

            //update apiDefinitionUrls
            foreach (var apiDefinitionUrl in definition.SelectTokens("$..metadata.apiDefinitionUrl"))
            {
                var defaultValue = (jsonObject["parameters"]).SelectTokens("$..defaultValue")
                    .FirstOrDefault(p => apiDefinitionUrl.ToString().StartsWith(p.ToString()));
                if (defaultValue != null && !string.IsNullOrWhiteSpace(defaultValue.Value<string>()))
                {
                    var paramName = ((JProperty)defaultValue.Parent.Parent.Parent).Name;
                    var metadata = apiDefinitionUrl.Parent.Parent as dynamic;
                    metadata.apiDefinitionUrl =
                        $"[concat(parameters('{paramName}'), '{(metadata.apiDefinitionUrl.ToString().Replace(defaultValue.ToString(), "")).Replace("'", "', variables('quote'), '")}')]";
                }
            }

            //update paths
            foreach (var path in definition.SelectTokens("$.actions..inputs.path"))
            {
                var input = path.Parent.Parent;

                if (!input["host"]["connection"]["name"].Value<string>()
                    .Equals("@parameters('$connections')['sharepointonline']['connectionId']"))
                    continue;

                //search for default sharepoint Uri
                var defaultValue = (jsonObject["parameters"]).SelectTokens("$..defaultValue").FirstOrDefault(p =>
                    path.Value<string>()
                        .StartsWith($"/datasets/@{{encodeURIComponent(encodeURIComponent('{p.ToString()}'))}}"));
                if (defaultValue == null) continue;

                var paramName = ((JProperty)defaultValue.Parent.Parent.Parent).Name;
                //replace uri for parameter and escape quotes
                path.Replace("[concat('" + path.Value<string>().Replace("'", "', variables('quote'), '")
                                 .Replace($@"'{defaultValue}'", $@"parameters('{paramName}')") + "')]");
            }

            //update connections
            var connections = (resource["properties"]?["parameters"]?["$connections"]?["value"] as JObject)
                ?.Children<JProperty>();
            if (connections != null)
            {
                foreach (var c in connections)
                {
                    //determine name
                    var connectionName = c.Name;

                    //get connection object
                    var connection = c.First;

                    connection["connectionName"] = $"[variables('{connectionName}_Connection')]";
                    connection["connectionId"] =
                        $"[resourceId('Microsoft.Web/connections', variables('{connectionName}_Connection'))]";

                    if (!connection["id"].Value<string>().StartsWith("["))
                    {
                        string connectionType = Path.GetFileName(connection["id"].Value<string>());
                        connection["id"] =
                            $"[concat('/subscriptions/', subscription().subscriptionId, '/providers/Microsoft.Web/locations/', resourceGroup().location,'/managedApis/{connectionType}')]";
                    }

                    //add variable if not exist
                    if (jsonObject["variables"][$"{connectionName}_Connection"] == null)
                        jsonObject["variables"][$"{connectionName}_Connection"] = connectionName;

                    //update connection dependencies
                    if (resource["dependsOn"] != null)
                    {
                        var dependsOn = ((JArray)resource["dependsOn"]).Cast<JValue>();

                        foreach (var dependencie in dependsOn)
                        {
                            if (dependencie.Value<string>() ==
                                $"[resourceId('Microsoft.Web/connections', parameters('connections_{connectionName}_name'))]"
                            )
                            {
                                dependencie.Value =
                                    $"[resourceId('Microsoft.Web/connections', variables('{connectionName}_Connection'))]";
                            }
                        }
                    }
                }
            }
        }
    }
}
