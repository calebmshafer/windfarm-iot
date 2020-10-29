using Microsoft.Azure.WebJobs;
using Microsoft.Azure.EventHubs;
using System;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MachineLearning;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using Newtonsoft.Json;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using Azure.DigitalTwins.Core.Serialization;
using PhysicsModel;

using System.Net.Http;
using System.Net.Http.Headers;
namespace Doosan.Function
{

    struct DtIds {
        public string sensor;
        public string turbineObserved;
    }

    struct TemperatureValues {
        public float nacelle;
        public float gearBox;
        public float generator;
    }

    struct PowerValues {
        public float powerObserved;
        public float powerPM;
        public float powerDM;
    }

    public static class WindFarmIoT
    {
        private static DigitalTwinsClient client;
        private const string adtInstanceUrl = "https://windfarm-iot.api.wcus.digitaltwins.azure.net";
        private const int interpolationSteps = 6;

/*
        [FunctionName("WindFarmIoT")]
        public static async void RunWindFarmIoT([EventHubTrigger("iothub-m6vf5", Connection = "EventHubConnectionAppSetting")]EventData[] events, ILogger log)
        {
            if (client == null) Authenticate(log);
            var exceptions = new List<Exception>();
            foreach (EventData eventData in events) {
                try
                {
                    string messageBody = Encoding.UTF8.GetString(eventData.Body.Array, eventData.Body.Offset, eventData.Body.Count);
                    JObject messageData = JObject.Parse(messageBody); 
                    string deviceIdString = eventData.SystemProperties["iothub-connection-device-id"].ToString();
                    string deviceId = deviceIdString.Substring(deviceIdString.IndexOf('.') + 1);

                    await processSensorData(deviceId, messageData);

                    await Task.Yield();
                }
                catch (Exception e)
                {
                    // We need to keep processing the rest of the batch - capture this exception and continue.
                    // Also, consider capturing details of the message that failed processing so it can be processed again later.
                    exceptions.Add(e);
                }
            }
        }
        */

        private static void Authenticate(ILogger log)
        {
            try
            {
                var credential = new DefaultAzureCredential();
                client = new DigitalTwinsClient(new Uri(adtInstanceUrl), credential);
            } catch(Exception e)
            {
                Console.WriteLine($"Authentication or client creation error: {e.Message}");
                Environment.Exit(0);
            }
        }

        private static async Task processSensorData(string deviceId, JObject sensorData) {
            
            // extract sensor data
            var info = new WTPowerRequestInfo { PowerInputs = new List<WTInfo>() };
            info.PowerInputs.Add(new WTInfo {
                    Blade1PitchPosition = (float)sensorData.GetValue("pitchAngle1"),
                    Blade2PitchPosition = (float)sensorData.GetValue("pitchAngle2"),
                    Blade3PitchPosition = (float)sensorData.GetValue("pitchAngle3"),
                    OriginSysTime = (string)sensorData.GetValue("originSysTime"),
                    WindDir = (float)sensorData.GetValue("windDirection"),
                    WindSpeed = (float)sensorData.GetValue("windSpeed"),
                    YawPosition = (float)sensorData.GetValue("yawPosition")
            });

            var tempValues = new TemperatureValues() {
                nacelle = (float)sensorData.GetValue("nacelleTemp"),
                gearBox = (float)sensorData.GetValue("gearboxTemp"),
                generator = (float)sensorData.GetValue("convTemp"),
            };

            // update sensor data on ADT
            string query = $"SELECT * FROM DigitalTwins T WHERE IS_OF_MODEL(T, 'dtmi:adt:chb:Sensor;1') AND T.deviceId = '{deviceId}'";
            DtIds dtIds = await fetchDtIds(query);
            if (dtIds.sensor == null || dtIds.turbineObserved == null) return;
            client.UpdateDigitalTwin(dtIds.sensor, generatePatchForSensor(info.PowerInputs[0], tempValues));

            // update turbine data on ADT. We return index 0 since only a single
            // value should only be processed.
            var powerValues = new PowerValues() {
                powerObserved = (float)sensorData.GetValue("power"),
                powerDM = (float)(await MlApi.GetPowerAsync(info.PowerInputs)).result[0],
                powerPM = (float)(await PmAPI.GetPowerAsync(info.PowerInputs))[0]
            };
            client.UpdateDigitalTwin(dtIds.turbineObserved, generatePatchForTurbine(powerValues));
        }

        private static async Task<DtIds> fetchDtIds(string query) {
            DtIds dtIds = new DtIds();

            try { 
                Azure.AsyncPageable<string> result = client.QueryAsync(query);
                IAsyncEnumerator<Azure.Page<string>> enumerator = result.AsPages().GetAsyncEnumerator();
                while (await enumerator.MoveNextAsync()) {
                    IReadOnlyList<string> values = enumerator.Current.Values;
                    if (values.Count > 0) {
                        JObject nodeData = JObject.Parse(values[0]); 
                        dtIds.sensor = (string)nodeData["$dtId"];
                        dtIds.turbineObserved = (string)nodeData["observes"];
                    } else throw new Exception("Node not found!");
                }
             } catch {}
            return dtIds;
        }

        private static string generatePatchForSensor(WTInfo info, TemperatureValues tempValues) {
            UpdateOperationsUtility uou = new UpdateOperationsUtility();

            uou.AppendReplaceOp("/blade1PitchAngle", info.Blade1PitchPosition);
            uou.AppendReplaceOp("/blade2PitchAngle", info.Blade2PitchPosition);
            uou.AppendReplaceOp("/blade3PitchAngle", info.Blade3PitchPosition);
            uou.AppendReplaceOp("/yawPosition", info.YawPosition);
            uou.AppendReplaceOp("/windDirection", info.WindDir);
            uou.AppendReplaceOp("/windSpeed", info.WindSpeed);
            uou.AppendReplaceOp("/temperatureNacelle", tempValues.nacelle);
            uou.AppendReplaceOp("/temperatureGenerator", tempValues.generator);
            uou.AppendReplaceOp("/temperatureGearBox", tempValues.gearBox);

            return uou.Serialize();
        }

        private static string generatePatchForTurbine(PowerValues powerValues) {
            UpdateOperationsUtility uou = new UpdateOperationsUtility();

            uou.AppendReplaceOp("/powerObserved", powerValues.powerObserved);
            uou.AppendReplaceOp("/powerPM", powerValues.powerPM);
            uou.AppendReplaceOp("/powerDM", powerValues.powerDM);

            return uou.Serialize();
        }

        [FunctionName("TriggerPrediction")]
        public static async Task<IActionResult> HttpTriggerPrediction(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]
            HttpRequest req, ILogger log)
        {
            log.LogInformation("Machine Learning HTTP trigger function processed a request.");

            try {

                /* WIP: Make request to predictiondata endpoint. The endpoint is broken.
                using (var client = new HttpClient()) {
                    client.BaseAddress = new Uri("http://52.157.19.187/api/predictiondata");
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    HttpResponseMessage response = await client.GetAsync(client.BaseAddress);
                }
                */

                /*
                Example request body:
                {     
                    "PowerInputs": [{
                        "Blade1PitchPosition": 1.99,
                        "Blade2PitchPosition": 2.02,
                        "Blade3PitchPosition": 1.92,
                        "OriginSysTime": "7/29/2018 11:43:00",
                        "WindDir": -8.6,
                        "WindSpeed": 6.66,
                        "YawPosition": 5.05
                    },{
                        "Blade1PitchPosition": 3.1,
                        "Blade2PitchPosition": 2.1,
                        "Blade3PitchPosition": 1.2,
                        "OriginSysTime": "7/29/2018 11:43:01",
                        "WindDir": -8.6,
                        "WindSpeed": 6.66,
                        "YawPosition": 5.05
                    }]
                }
                */

                /* Sample incoming prediction data.
                [
                    {"forecastDateTime":"2020-10-29T00:00:00","windspeed":5.9,"winddirection":-1,"yawposition":-131.48,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T03:00:00","windspeed":6.8,"winddirection":3,"yawposition":-109.37,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T06:00:00","windspeed":6.3,"winddirection":5,"yawposition":-104.41,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T09:00:00","windspeed":5.9,"winddirection":5,"yawposition":-104.41,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T12:00:00","windspeed":6.9,"winddirection":2,"yawposition":-109.37,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T15:00:00","windspeed":7.1,"winddirection":-4,"yawposition":-131.48,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T18:00:00","windspeed":6.8,"winddirection":-4,"yawposition":-131.48,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2},
                    {"forecastDateTime":"2020-10-29T21:00:00","windspeed":6.8,"winddirection":-3,"yawposition":-131.48,"bladepitch1":2,"bladepitch2":2,"bladepitch3":2}
                ]
                */

                // We'll hardcode raw data since prediction endpoint is broken.
                string predictionRequest = @"[
{""forecastDateTime"":""2020-10-29T00:00:00"",""windspeed"":5.9,""winddirection"":-1,""yawposition"":-131.48,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T03:00:00"",""windspeed"":6.8,""winddirection"":3,""yawposition"":-109.37,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T06:00:00"",""windspeed"":6.3,""winddirection"":5,""yawposition"":-104.41,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T09:00:00"",""windspeed"":5.9,""winddirection"":5,""yawposition"":-104.41,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T12:00:00"",""windspeed"":6.9,""winddirection"":2,""yawposition"":-109.37,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T15:00:00"",""windspeed"":7.1,""winddirection"":-4,""yawposition"":-131.48,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T18:00:00"",""windspeed"":6.8,""winddirection"":-4,""yawposition"":-131.48,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2},
{""forecastDateTime"":""2020-10-29T21:00:00"",""windspeed"":6.8,""winddirection"":-3,""yawposition"":-131.48,""bladepitch1"":2,""bladepitch2"":2,""bladepitch3"":2}
]";

                dynamic predictionData = JsonConvert.DeserializeObject(predictionRequest);

                // We need interpolation here, for 48 points that's every 30 minutes.
                WTPowerRequestInfo predictionInput = new WTPowerRequestInfo { PowerInputs = new List<WTInfo>() };

                for (int i = 0; i < predictionData.Count; ++i)
                {
                    // Interpolation occurs here, we add interpolationSteps* additional points to each data point.
                    for (int j = 0; j < interpolationSteps; ++j) {

                        // No need for time calculation here assuming interpolationSteps will remain at 6.
                        DateTime d1 = DateTime.Parse((string)predictionData[i].forecastDateTime);
                        DateTime interpolatedDate = d1.AddMinutes(j * 30);

                        float yawPositionCurrent = predictionData[i].yawposition;
                        float interpolatedYaw = yawPositionCurrent;

                        float windSpeedCurrent = predictionData[i].windspeed;
                        float interpolatedWindSpeed = windSpeedCurrent;

                        float windDirectionCurrent = predictionData[i].winddirection;
                        float interpolatedWindDirection = windDirectionCurrent;

                        // No data to interpolate to on last data point.
                        if (i != predictionData.Count - 1)
                        {
                            int next = i + 1;
                            float yawPositionNext = predictionData[next].yawposition;
                            interpolatedYaw = interpolateData(yawPositionCurrent, yawPositionNext, j);

                            float windSpeedNext = predictionData[next].windspeed;
                            interpolatedWindSpeed = interpolateData(windSpeedCurrent, windSpeedNext, j);

                            float windDirectionNext = predictionData[next].winddirection;
                            interpolatedWindDirection = interpolateData(windDirectionCurrent, windDirectionNext, j);
                        }

                        // No need to interpolate blade angles since they remain constant.
                        predictionInput.PowerInputs.Add(new WTInfo
                        {
                            Blade1PitchPosition = predictionData[i].bladepitch1,
                            Blade2PitchPosition = predictionData[i].bladepitch2,
                            Blade3PitchPosition = predictionData[i].bladepitch3,
                            OriginSysTime = interpolatedDate.ToString(),
                            WindDir = interpolatedWindDirection,
                            WindSpeed = interpolatedWindSpeed,
                            YawPosition = interpolatedYaw
                        });
                    }
                }

                DMResultInfo PredictionPowerDMSet = await MlApi.GetPowerAsync(predictionInput.PowerInputs);
                float[] PredictionPowerPMSet = await PmAPI.GetPowerAsync(predictionInput.PowerInputs);

                WTPowerResultSet results = new WTPowerResultSet { powerResults = new List<WTPowerResult>() };

                int iterator = 0;
                foreach (WTInfo powerInfo in predictionInput.PowerInputs)
                {
                    results.powerResults.Add(new WTPowerResult
                    {
                        OriginSysTime = powerInfo.OriginSysTime,
                        Power_DM = (float)PredictionPowerDMSet.result[iterator],
                        Power_PM = (float)PredictionPowerPMSet[iterator]
                    });

                    ++iterator;
                }


                return (ActionResult)new OkObjectResult(results);
            }
            catch (Exception e)
            {
                return new BadRequestObjectResult(e.ToString());
            }
        }

        private static float interpolateData(float currentValue, float nextValue, int step) {
            float valueDifference = Math.Abs(nextValue - currentValue);
            float stepDifference = valueDifference / (interpolationSteps + 1);

            if (currentValue < nextValue)
            {
                return currentValue + (stepDifference * step);
            }
            else
            {
                return currentValue - (stepDifference * step);
            }
        }

    }
}
