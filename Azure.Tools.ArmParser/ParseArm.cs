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
        internal static void Execute(string sourceFile)
        {

            var text = File.ReadAllText(sourceFile);
            var jsonObject = JObject.Parse(text, new JsonLoadSettings {CommentHandling = CommentHandling.Load});


            foreach (JObject resource in jsonObject["resources"])
            {
                //skip if condition not met
                if (!(resource is JObject)) continue;
                if (resource["type"].Value<string>() != "Microsoft.Logic/workflows") continue;

                //define name
                string name = $"{resource["name"]}".Replace("[parameters('workflows_", "").Replace("_name')]", "");


                //define definition
                var definition = (JObject) resource?["properties"]?["definition"];

                //remove dependings of websites, not in this ARM
                if (resource["dependsOn"] != null)
                    for (var i = 0; i < ((JArray) resource["dependsOn"]).Count; i++)
                    {
                        var dependencie = ((JArray) resource["dependsOn"])[i];
                        if (dependencie.Value<string>().StartsWith("[resourceId('Microsoft.Web/sites'"))
                        {
                            ((JArray) resource["dependsOn"]).Remove(dependencie);
                        }
                    }

                //add resources if not add yet
                if (resource["resources"] == null)
                {
                    var l = (resource?["properties"]).Parent;
                    l.AddAfterSelf(new JProperty("resources", new JArray()));
                }


                //add OMS if not added yet, else replace
                var resources = ((JArray) resource?["resources"]);
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
                    var l = ((JToken) resource["location"]).Parent;
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
                        var paramName = ((JProperty) defaultValue.Parent.Parent.Parent).Name;
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
                        var paramName = ((JProperty) defaultValue.Parent.Parent.Parent).Name;
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

                    var paramName = ((JProperty) defaultValue.Parent.Parent.Parent).Name;
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
                            var dependsOn = ((JArray) resource["dependsOn"]).Cast<JValue>();

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

            string output =
                ((JObject) jsonObject).ToString(Newtonsoft.Json.Formatting
                    .Indented); //JsonConvert.SerializeObject(jsonObject, Formatting.Indented);



            //find replace
            var parseItems = new Dictionary<string, string>
            {
                //{"\"location\": \"westeurope\"", "\"location\": \"[resourceGroup().location]\""},
                //{"\"interval\": 3", "\"interval\": \"[parameters(\'LogicAppTriggerInterVal\')]\""},

                //{"[parameters(\'workflows_ProcessWoningStatus_name\')]", "ProcessWoningStatus"},
                //{"[parameters(\'workflows_ProcessAnnulering_name\')]", "ProcessAnnulering"},
                //{"[parameters(\'workflows_ProcessAnnuleringItem_name\')]", "ProcessAnnuleringItem"},
                //{"[parameters(\'workflows_ProcessBericht_name\')]", "ProcessBericht"},
                //{"[parameters(\'workflows_SaveGmfDocuments_name\')]", "SaveGmfDocuments"},
                //{"[parameters(\'workflows_ProcessProject_name\')]", "ProcessProject"},
                //{"[parameters(\'workflows_ProcessGMFEvents_name\')]", "ProcessGMFEvents"},
                //{"[parameters(\'workflows_SaveGmfFotos_name\')]", "SaveGmfFotos"},
                //{"[parameters(\'workflows_LoopingRegioCodes_name\')]", "LoopingRegioCodes"},
                //{"[parameters(\'workflows_ProcessOpdracht_name\')]", "ProcessOpdracht"},
                //{"[parameters(\'workflows_ProcessGmfUsers_name\')]", "ProcessGmfUsers"},
                //{"[parameters(\'workflows_ProcessBeoordelingTGereed_name\')]", "ProcessBeoordelingTGereed"},
                //{"[parameters(\'workflows_ProcessBeoordelingAGAsset_name\')]", "ProcessBeoordelingAGAsset"},
                //{"[parameters(\'workflows_ProcessBeoordelingAGProductiestaat_name\')]", "ProcessBeoordelingAGProductiestaat"},
                //{"[parameters(\'workflows_ProcessOpdrachtGereed_name\')]", "ProcessOpdrachtGereed"},
                //{"[parameters(\'workflows_ProcessBijstellingsVerzoek_name\')]", "ProcessBijstellingsVerzoek"},

                ////servicebus
                //{"\"connectionName\": \"servicebus\"", "\"connectionName\": \"[variables(\'servicebus_Connection\')]\""},
                //{"\"id\": \"/subscriptions/c70cc5ad-f0e6-464d-92c9-0546446d47a3/providers/Microsoft.Web/locations/westeurope/managedApis/servicebus\"", "\"id\": \"[concat(\'/subscriptions/\', subscription().subscriptionId, \'/providers/Microsoft.Web/locations/\', resourceGroup().location, \'/managedApis/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_SaveGmfDocuments_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessGMFEvents_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_SaveGmfFotos_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBericht_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessOpdracht_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessAnnulering_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingTGereed_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingAGAsset_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingAGProductiestaat_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessOpdrachtGereed_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'servicebus_Connection\'))]\""},

                ////sql 
                //{"\"connectionName\": \"sql\"", "\"connectionName\": \"[variables(\'sql_Connection\')]\""},
                //{"\"id\": \"/subscriptions/c70cc5ad-f0e6-464d-92c9-0546446d47a3/providers/Microsoft.Web/locations/westeurope/managedApis/sql\"", "\"id\": \"[concat(\'/subscriptions/\', subscription().subscriptionId, \'/providers/Microsoft.Web/locations/\', resourceGroup().location,\'/managedApis/\', variables(\'sql_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_LoopingRegioCodes_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessOpdracht_connectionId_2\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessAnnulering_connectionId_2\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{ "\"connectionId\": \"[parameters(\'workflows_ProcessBericht_connectionId_2\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{ "\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingTGereed_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{ "\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingAGAsset_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{ "\"connectionId\": \"[parameters(\'workflows_ProcessBeoordelingAGProductiestaat_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},
                //{ "\"connectionId\": \"[parameters(\'workflows_ProcessOpdrachtGereed_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sql_Connection\'))]\""},

                ////sharepoint
                //{"\"connectionName\": \"sharepointonline\"", "\"connectionName\": \"[variables(\'sharepointonline_Connection\')]\""},
                //{"\"SlugId\": \"0x0120D520002B1AB996B640F14583806085B77AE259002D0BFB56143CAF42A0BAB14FD6C0FC8F\"", "\"SlugId\": \"[parameters(\'SlugIdProjectDossier\')]\""},
                //{"\"SlugId\": \"0x0120D520006D393204B776B847B728F953A496256D007E892E4B03B47B44AEAB93CEA2C59A7D\"", "\"SlugId\": \"[parameters(\'SlugIdOpdrachtDossier\')]\""},
                //{"\"id\": \"/subscriptions/c70cc5ad-f0e6-464d-92c9-0546446d47a3/providers/Microsoft.Web/locations/westeurope/managedApis/sharepointonline\"", "\"id\": \"[concat(\'/subscriptions/\', subscription().subscriptionId, \'/providers/Microsoft.Web/locations/\', resourceGroup().location, \'/managedApis/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessAnnuleringItem_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessWoningStatus_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBericht_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessProject_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessOpdracht_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessAnnulering_connectionId_1\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},
                //{"\"connectionId\": \"[parameters(\'workflows_ProcessBijstellingsVerzoek_connectionId\')]\"", "\"connectionId\": \"[concat(resourceGroup().id, \'/providers/Microsoft.Web/connections/\', variables(\'sharepointonline_Connection\'))]\""},

                //datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Woningen'))}/items/@{triggerBody()?['Object']?['ID']}

                //{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/files/@{encodeURIComponent(encodeURIComponent('/Combi/'))}@{triggerBody()?['Object']?['Title']}/content" , "[replace(concat('/datasets/@{encodeURIComponent(encodeURIComponent(', variables('quote'), parameters('sharepointSite'), variables('quote'), '))}/files/@{encodeURIComponent(encodeURIComponent([Q]/Combi/[Q]))}@{triggerBody()?[[Q]Object[Q]]?[[Q]Title[Q]]}/content'), '[Q]', variables('quote'))]"},
                ////{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Woningen'))}/items/@{triggerBody()?['ID']}" , "[replace(replace(concat(variables('sharepointLib'), '/items/@{triggerBody()?[[Q]ID[Q]]}'), '[LIBNAME]','Woningen'), '[Q]', variables('quote'))]"},
                ////{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Woningen'))}/items/@{triggerBody()?['Object']?['ID']}" , "[replace(replace(concat(variables('sharepointLib'), '/items/@{triggerBody()?[[Q]Object[Q]]?[[Q]ID[Q]]}'), '[LIBNAME]','Woningen'), '[Q]', variables('quote'))]"},
                ////{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Woningen'))}/onupdateditems" , "[replace(concat(variables(\'sharepointLib\'), \'/onupdateditems\' ), \'[LIBNAME]\',\'Woningen\')]"},
                ////{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Annuleringen'))}/onupdateditems" , "[replace(concat(variables(\'sharepointLib\'), \'/onupdateditems\' ), \'[LIBNAME]\',\'Annuleringen\')]"},
                ////{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Berichten'))}/onupdateditems" , "[replace(concat(variables(\'sharepointLib\'), \'/onupdateditems\' ), \'[LIBNAME]\',\'Berichten\')]"},
                //{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Opdrachten'))}/items/@{body('OpdrachtExists').ListId}" , "[replace(replace(concat(variables(\'sharepointLib\'), \'/items/@{body([Q]OpdrachtExists[Q]).ListId}\'), \'[LIBNAME]\',\'Opdrachten\'), \'[Q]\', variables(\'quote\'))]"},
                //{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/tables/@{encodeURIComponent(encodeURIComponent('Opdrachten'))}/items" , "[replace(concat(variables('sharepointLib'), '/items'), '[LIBNAME]','Opdrachten')]"},
                //{ "/datasets/@{encodeURIComponent(encodeURIComponent('https://baasbv.sharepoint.com/sites/DSP-Test/'))}/GetFileContentByPath" , "[concat('/datasets/@{encodeURIComponent(encodeURIComponent(', variables('quote'), parameters('sharepointSite'), variables('quote'),'))}/GetFileContentByPath')]"},

                ////dspsend
                //{ "https://devbaasdspdspsend.azurewebsites.net/swagger/docs/v1", "[concat(parameters(\'sendrequestsDspUrl\'), \'swagger/docs/v1\')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/AnnuleringGereed", "[concat(parameters(\'sendrequestsDspUrl\'), \'api/AnnuleringGereed\')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/TG", "[concat(parameters('sendrequestsDspUrl'), 'api/TG')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/AGProductiestaat", "[concat(parameters('sendrequestsDspUrl'), 'api/AGProductiestaat')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/AgAsset", "[concat(parameters('sendrequestsDspUrl'), 'api/AgAsset')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/Bijstelling", "[concat(parameters('sendrequestsDspUrl'), 'api/Bijstelling')]"},
                //{ "https://devbaasdspdspsend.azurewebsites.net/api/Planning", "[concat(parameters('sendrequestsDspUrl'), 'api/Planning')]"},

                ////actosend 
                //{ "https://devbaasdspactosend.azurewebsites.net/api/Opdracht/@{encodeURIComponent('11')}" , "[concat(parameters('sendrequestsActoUrl'), 'api/Opdracht/@{encodeURIComponent(', variables('quote'),'11', variables('quote'),')}')]"},
                //{ "https://devbaasdspactosend.azurewebsites.net/api/Opdracht/@{encodeURIComponent('5')}" , "[concat(parameters('sendrequestsActoUrl'), 'api/Opdracht/@{encodeURIComponent(', variables('quote'),'5', variables('quote'),')}')]"},

                //{ "https://devbaasdspactosend.azurewebsites.net:443/swagger/docs/v1", "[concat(parameters(\'sendrequestsActoUrl\'), \'swagger/docs/v1\')]"},
                //{ "https://devbaasdspactosend.azurewebsites.net/swagger/docs/v1", "[concat(parameters(\'sendrequestsActoUrl\'), \'swagger/docs/v1\')]"},
                //{ "https://devbaasdspactosend.azurewebsites.net/api/Opdracht", "[concat(parameters('sendrequestsActoUrl'), 'api/Opdracht')]"},
                //{ "https://devbaasdspactosend.azurewebsites.net/api/Project", "[concat(parameters('sendrequestsActoUrl'), 'api/Project')]"},

                ////GMF
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/company/@{encodeURIComponent('Baas')}/user/@{encodeURIComponent(item().Login)}", "[concat(parameters('gmf_api_uri'), '/api/company/@{encodeURIComponent(', variables('quote'),'Baas', variables('quote'),')}/user/@{encodeURIComponent(item().Login)}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/company/@{encodeURIComponent('Baas')}/regions", "[concat(parameters('gmf_api_uri'), '/api/company/@{encodeURIComponent(', variables('quote'),'Baas', variables('quote'),')}/regions')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{encodeURIComponent(item()['RegionCode'])}/project/@{encodeURIComponent(item()['IntegrationId'])}/documents", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{encodeURIComponent(item()[', variables('quote'),'RegionCode', variables('quote'),'])}/project/@{encodeURIComponent(item()[', variables('quote'),'IntegrationId', variables('quote'),'])}/documents')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{encodeURIComponent(item()['RegionCode'])}/@{encodeURIComponent(item()['IntegrationId'])}/photos", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{encodeURIComponent(item()[', variables('quote'),'RegionCode', variables('quote'),'])}/@{encodeURIComponent(item()[', variables('quote'),'IntegrationId', variables('quote'),'])}/photos')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{item()?['GMFCode']}/events/@{body('GetLastRunDateTime')?['OutputParameters']?['OldLastRunDateTimeOut']}/@{body('GetLastRunDateTime')?['OutputParameters']?['NewLastRunDateTimeOut']}", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{item()?[', variables('quote'),'GMFCode', variables('quote'),']}/events/@{body(', variables('quote'),'GetLastRunDateTime', variables('quote'),')?[', variables('quote'),'OutputParameters', variables('quote'),']?[', variables('quote'),'OldLastRunDateTimeOut', variables('quote'),']}/@{body(', variables('quote'),'GetLastRunDateTime', variables('quote'),')?[', variables('quote'),'OutputParameters', variables('quote'),']?[', variables('quote'),'NewLastRunDateTimeOut', variables('quote'),']}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{encodeURIComponent(triggerBody()['Properties']?['RegioCode'])}/photos/@{encodeURIComponent(item()?['Id'])}", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{encodeURIComponent(triggerBody()[', variables('quote'),'Properties', variables('quote'),']?[', variables('quote'),'RegioCode', variables('quote'),'])}/photos/@{encodeURIComponent(item()?[', variables('quote'),'Id', variables('quote'),'])}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{encodeURIComponent(triggerBody()['Properties']?['RegioCode'])}/document/@{encodeURIComponent(item()['Id'])}", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{encodeURIComponent(triggerBody()[', variables('quote'),'Properties', variables('quote'),']?[', variables('quote'),'RegioCode', variables('quote'),'])}/document/@{encodeURIComponent(item()[', variables('quote'),'Id', variables('quote'),'])}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()['Properties']?['RegioCode']}/document/@{item()['Id']}", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()[', variables('quote'),'Properties', variables('quote'),']?[', variables('quote'),'RegioCode', variables('quote'),']}/document/@{item()[', variables('quote'),'Id', variables('quote'),']}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}/status", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}/status')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}/inspection", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}/inspection')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}/fieldwork", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}/fieldwork')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}/form", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}/form')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}/data", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}/data')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project/@{triggerBody()?['IntegrationId']}", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project/@{triggerBody()?[', variables('quote'),'IntegrationId', variables('quote'),']}')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{triggerBody()?['Rayon']?['GMFCode']}/project", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{triggerBody()?[', variables('quote'),'Rayon', variables('quote'),']?[', variables('quote'),'GMFCode', variables('quote'),']}/project')]"},
                //{ "https://gcibrokerstageapi.azurewebsites.net/api/gmf/@{encodeURIComponent(triggerBody()?.Rayon?.GMFCode)}/@{encodeURIComponent(triggerBody()?.IntegrationId)}/attachment", "[concat(parameters('gmf_api_uri'), '/api/gmf/@{encodeURIComponent(triggerBody()?.Rayon?.GMFCode)}/@{encodeURIComponent(triggerBody()?.IntegrationId)}/attachment')]"},
                //{ "\"PlatformUser\": \"@{coalesce(body(\'GetGmfLoginname\')?[\'Object\']?[\'Login\'], \'baas.stage.api\')}\"", "\"PlatformUser\": \"[replace(concat(\'@{coalesce(body([Q]GetGmfLoginname[Q])?[[Q]Object[Q]]?[[Q]Login[Q]], [Q]\',parameters(\'gmf_api_user\'),\'[Q])}\'), \'[Q]\', variables(\'quote\'))]\""},

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

            File.WriteAllText(sourceFile, output);
        }
    }
}
