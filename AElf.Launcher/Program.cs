﻿using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using AElf.ABI.CSharp;
using AElf.Cryptography;
using AElf.Cryptography.ECDSA;
using AElf.Database;
using AElf.Database.Config;
using AElf.Kernel;
using AElf.Kernel.Concurrency.Execution;
using AElf.Kernel.Concurrency.Execution.Messages;
using AElf.Kernel.KernelAccount;
using AElf.Kernel.Miner;
using AElf.Kernel.Modules.AutofacModule;
using AElf.Kernel.Node;
using AElf.Kernel.Node.Config;
using AElf.Kernel.Services;
using AElf.Kernel.TxMemPool;
using AElf.Network.Config;
using AElf.Runtime.CSharp;
using Autofac;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using IContainer = Autofac.IContainer;
using ServiceStack;

namespace AElf.Launcher
{
    class Program
    {
        private const string filepath = @"ChainInfo.json";
        private const string dir = @"Contracts";
        
        static void Main(string[] args)
        {
            // Parse options
            ConfigParser confParser = new ConfigParser();
            bool parsed;
            try
            {
                parsed = confParser.Parse(args);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

            
            if (!parsed)
                return;
            
            var txPoolConf = confParser.TxPoolConfig;
            var netConf = confParser.NetConfig;
            var databaseConf = confParser.DatabaseConfig;
            var minerConfig = confParser.MinerConfig;
            var nodeConfig = confParser.NodeConfig;
            var isMiner = confParser.IsMiner;
            var isNewChain = confParser.NewChain;
            var initData = confParser.InitData;
            
            var runner = new SmartContractRunner("../AElf.SDK.CSharp/bin/Debug/netstandard2.0/");
            var smartContractRunnerFactory = new SmartContractRunnerFactory();
            smartContractRunnerFactory.AddRunner(0, runner);
            
            // Setup ioc 
            IContainer container = SetupIocContainer(isMiner, isNewChain, netConf, databaseConf, txPoolConf, 
                minerConfig, nodeConfig, smartContractRunnerFactory);

            if (container == null)
            {
                Console.WriteLine("IoC setup failed");
                return;
            }

            if (!CheckDBConnect(container))
            {
                Console.WriteLine("Database connection failed");
                return;
            }

            // todo : quick fix, to be refactored
            
            ECKeyPair nodeKey = null;
            if (!string.IsNullOrWhiteSpace(confParser.NodeAccount))
            {
                try
                {
                    AElfKeyStore ks = new AElfKeyStore(nodeConfig.DataDir);

                    string pass = AskInvisible(confParser.NodeAccount);
                    ks.OpenAsync(confParser.NodeAccount, pass, false);

                    nodeKey = ks.GetAccountKeyPair(confParser.NodeAccount);
                }
                catch (Exception e)
                {
                    throw new Exception("Load keystore failed");
                }
            }

            using(var scope = container.BeginLifetimeScope())
            {
                IAElfNode node = scope.Resolve<IAElfNode>();
               
                // Start the system
                node.Start(nodeKey, confParser.Rpc, initData, SmartContractZeroCode);

                Console.ReadLine();
            }
            
        }

        
        private static byte[] SmartContractZeroCode
        {
            get
            {
                var ContractZeroName = "AElf.Kernel.Tests.TestContractZero";
                
                //var contractZeroDllPath = $"{dir}/{ContractZeroName}.dll";
                
                var contractZeroDllPath = $"../{ContractZeroName}/bin/Debug/netstandard2.0/{ContractZeroName}.dll";
                
                byte[] code = null;
                using (FileStream file = File.OpenRead(System.IO.Path.GetFullPath(contractZeroDllPath)))
                {
                    code = file.ReadFully();
                }
                /*ABI.CSharp.Module module = Generator.GetABIModule(code);
                string actual = new JsonFormatter(new JsonFormatter.Settings(true)).Format(module);
                Console.WriteLine(actual);*/
                return code;
            }
        }
        
        
        private static IContainer SetupIocContainer(bool isMiner, bool isNewChain, IAElfNetworkConfig netConf,
            IDatabaseConfig databaseConf, ITxPoolConfig txPoolConf, IMinerConfig minerConf, INodeConfig nodeConfig,
            SmartContractRunnerFactory smartContractRunnerFactory)
        {
            var builder = new ContainerBuilder();
            
            // Register everything
            builder.RegisterModule(new MainModule()); // todo : eventually we won't need this
            
            // Module registrations
            builder.RegisterModule(new TransactionManagerModule());
            builder.RegisterModule(new LoggerModule());
            builder.RegisterModule(new DatabaseModule(databaseConf));
            builder.RegisterModule(new NetworkModule(netConf, isMiner));
            builder.RegisterModule(new RpcServerModule());

            // register SmartContractRunnerFactory 
            builder.RegisterInstance(smartContractRunnerFactory).As<ISmartContractRunnerFactory>().SingleInstance();

            // register actor system
            /*ActorSystem sys = ActorSystem.Create("AElf");
            builder.RegisterInstance(sys).As<ActorSystem>().SingleInstance();*/
            
            Hash chainId;
            if (isNewChain)
            {
                chainId = Hash.Generate();
                JObject obj = new JObject(new JProperty("id", chainId.ToByteString().ToBase64()));

                // write JSON directly to a file
                using (StreamWriter file = File.CreateText(filepath))
                using (JsonTextWriter writer = new JsonTextWriter(file))
                {
                    obj.WriteTo(writer);
                }
            }
            else
            {
                // read JSON directly from a file
                using (StreamReader file = File.OpenText(filepath))
                using (JsonTextReader reader = new JsonTextReader(file))
                {
                    JObject chain = (JObject)JToken.ReadFrom(reader);
                    chainId = new Hash(Convert.FromBase64String(chain.GetValue("id").ToString()));
                }
            }

            // register miner config
            var minerConfiguration = isMiner ? minerConf : MinerConfig.Default;
            minerConfiguration.ChainId = chainId;
            builder.RegisterModule(new MinerModule(minerConfiguration));

            nodeConfig.ChainId = chainId;
            builder.RegisterModule(new MainChainNodeModule(nodeConfig));

            txPoolConf.ChainId = chainId;
            builder.RegisterModule(new TxPoolServiceModule(txPoolConf));
            
            
            IContainer container = null;
            
            try
            {
                container = builder.Build();
            }
            catch (Exception e)
            {
                return null;
            }

            return container;
        }

        private static bool CheckDBConnect(IContainer container)
        {
            var db = container.Resolve<IKeyValueDatabase>();
            return db.IsConnected();
        }
        
        public static string AskInvisible(string prefix)
        {
            Console.Write("Node account password: ");
            
            var pwd = new SecureString();
            while (true)
            {
                ConsoleKeyInfo i = Console.ReadKey(true);
                if (i.Key == ConsoleKey.Enter)
                {
                    break;
                }
                else if (i.Key == ConsoleKey.Backspace)
                {
                    if (pwd.Length > 0)
                    {
                        pwd.RemoveAt(pwd.Length - 1);
                    }
                }
                else
                {
                    pwd.AppendChar(i.KeyChar);
                    //Console.Write("*");
                }
            }
            
            Console.WriteLine();
            
            return new NetworkCredential("", pwd).Password;
        }
    }
}