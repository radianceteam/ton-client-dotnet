﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TonSdk.Modules;
using Xunit;
using Xunit.Abstractions;

namespace TonSdk.Tests.Modules
{
    public class NetModuleTests : IDisposable
    {
        private readonly ITonClient _client;
        private readonly ILogger _logger;

        public NetModuleTests(ITestOutputHelper outputHelper)
        {
            _logger = new XUnitTestLogger(outputHelper);
            _client = TestClient.Create(_logger);
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        [EnvDependentFact]
        public async Task Should_Query_Collection_Block_Signature()
        {
            var result = await _client.Net.QueryCollectionAsync(new ParamsOfQueryCollection
            {
                Collection = "blocks_signatures",
                Filter = JToken.FromObject(new { }),
                Result = "id",
                Limit = 1
            });

            Assert.NotNull(result);
        }

        [EnvDependentFact]
        public async Task Should_Query_Collection_All_Accounts()
        {
            var result = await _client.Net.QueryCollectionAsync(new ParamsOfQueryCollection
            {
                Collection = "accounts",
                Filter = JToken.FromObject(new { }),
                Result = "id balance"
            });

            Assert.NotNull(result);
            Assert.NotEmpty(result.Result);
        }

        [EnvDependentFact]
        public async Task Should_Query_Collection_Ranges()
        {
            var result = await _client.Net.QueryCollectionAsync(new ParamsOfQueryCollection
            {
                Collection = "messages",
                Filter = JToken.FromObject(new
                {
                    created_at = new
                    {
                        gt = 1562342740
                    }
                }),
                Result = "body created_at"
            });

            Assert.NotNull(result);
            Assert.NotEmpty(result.Result);
            Assert.True(result.Result[0].Value<ulong>("created_at") > 1562342740);
        }

        [EnvDependentFact]
        public async Task Should_Wait_For_Collection()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var request = TestClient.Create(_logger).Net
                .WaitForCollectionAsync(new ParamsOfWaitForCollection
                {
                    Collection = "transactions",
                    Filter = JObject.FromObject(new
                    {
                        now = new { gt = now }
                    }),
                    Result = "id now"
                });

            var task = _client.GetGramsFromGiverAsync(TestClient.GiverAddress);

            var result = await request;
            Assert.NotNull(result);

            await task;
        }

        [EnvDependentFact]
        public async Task Should_Subscribe_For_Transactions_With_Address()
        {
            var keys = await _client.Crypto.GenerateRandomSignKeysAsync();

            var deployParams = new ParamsOfEncodeMessage
            {
                Abi = TestClient.Abi("Hello"),
                DeploySet = new DeploySet
                {
                    Tvc = TestClient.Tvc("Hello")
                },
                Signer = new Signer.Keys
                {
                    KeysProperty = keys
                },
                CallSet = new CallSet
                {
                    FunctionName = "constructor"
                }
            };

            var msg = await _client.Abi.EncodeMessageAsync(deployParams);
            var address = msg.Address;
            var transactionIds = new List<string>();

            var handle = await _client.Net.SubscribeCollectionAsync(new ParamsOfSubscribeCollection
            {
                Collection = "transactions",
                Filter = JObject.FromObject(new
                {
                    account_addr = new { eq = address },
                    status = new { eq = 3 } // Finalized
                }),
                Result = "id account_addr"
            }, (json, result) =>
            {
                Assert.Equal(100, result);
                Assert.NotNull(json);
                Assert.NotNull(json["result"]);
                Assert.Equal(address, json.SelectToken("result.account_addr"));
                transactionIds.Add((string)json.SelectToken("result.id"));
                return Task.CompletedTask;
            });

            await _client.DeployWithGiverAsync(deployParams);

            // give some time for subscription to receive all data
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Equal(2, transactionIds.Distinct().Count());

            await _client.Net.UnsubscribeAsync(handle);
        }

        [EnvDependentFact]
        public async Task Should_Subscribe_For_Messages()
        {
            var messages = new List<string>();

            var handle = await _client.Net.SubscribeCollectionAsync(new ParamsOfSubscribeCollection
            {
                Collection = "messages",
                Filter = JObject.FromObject(new
                {
                    dst = new { eq = "1" }
                }),
                Result = "id"
            }, (json, result) =>
            {
                Assert.Equal(100, result);
                Assert.NotNull(json);
                Assert.NotNull(json["result"]);
                messages.Add(json["result"].ToString());
                return Task.CompletedTask;
            });

            await _client.GetGramsFromGiverAsync(TestClient.GiverAddress);

            Assert.Empty(messages);

            await _client.Net.UnsubscribeAsync(handle);
        }

        [EnvDependentFact]
        public async Task Should_Run_Query()
        {
            var info = await _client.Net.QueryAsync(new ParamsOfQuery
            {
                Query = "query{info{version}}"
            });

            Assert.NotNull(info);
            Assert.NotNull(info.Result);

            var version = info.Result["data"]?["info"]?["version"]?.ToString();
            Assert.NotNull(version);
            Assert.NotEmpty(version);
            Assert.Equal(3, version.Split(".").Length);
        }

        [EnvDependentFact]
        public async Task Should_Suspend_Resume()
        {
            var keys = await _client.Crypto.GenerateRandomSignKeysAsync();
            var (abi, tvc) = TestClient.Package("Hello");

            var deployParams = new ParamsOfEncodeMessage
            {
                Abi = abi,
                DeploySet = new DeploySet
                {
                    Tvc = tvc
                },
                Signer = new Signer.Keys
                {
                    KeysProperty = keys
                },
                CallSet = new CallSet
                {
                    FunctionName = "constructor"
                }
            };

            var msg = await _client.Abi.EncodeMessageAsync(deployParams);
            var address = msg.Address;
            var transactionIds = new List<string>();

            var subscriptionClient = TestClient.Create(_logger);
            var handle = await subscriptionClient.Net.SubscribeCollectionAsync(new ParamsOfSubscribeCollection
            {
                Collection = "transactions",
                Filter = JObject.FromObject(new
                {
                    account_addr = new { eq = address },
                    status = new { eq = 3 } // Finalized
                }),
                Result = "id account_addr"
            }, (json, result) =>
            {
                transactionIds.Add((string)json.SelectToken("result.id"));
                return Task.CompletedTask;
            });

            await _client.GetGramsFromGiverAsync(msg.Address);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Single(transactionIds);

            // suspend subscription
            await subscriptionClient.Net.SuspendAsync();

            // deploy to create second transaction
            await _client.Processing.ProcessMessageAsync(new ParamsOfProcessMessage
            {
                MessageEncodeParams = deployParams
            });

            // check that second transaction is not received when subscription suspended
            Assert.Single(transactionIds);

            // resume subscription
            await subscriptionClient.Net.ResumeAsync();

            // run contract function to create new transaction
            await _client.Processing.ProcessMessageAsync(new ParamsOfProcessMessage
            {
                MessageEncodeParams = new ParamsOfEncodeMessage
                {
                    Abi = abi,
                    Signer = new Signer.Keys
                    {
                        KeysProperty = keys
                    },
                    Address = msg.Address,
                    CallSet = new CallSet
                    {
                        FunctionName = "touch"
                    }
                },
                SendEvents = false
            });

            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Equal(2, transactionIds.Distinct().Count());

            await subscriptionClient.Net.UnsubscribeAsync(handle);
        }

        [EnvDependentFact]
        public async Task Should_Find_Last_Shard_Block()
        {
            var block = await _client.Net.FindLastShardBlockAsync(new ParamsOfFindLastShardBlock
            {
                Address = TestClient.GiverAddress
            });

            Assert.NotNull(block);
            Assert.NotEmpty(block.BlockId);
        }
    }
}
