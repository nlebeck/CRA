﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CRA.ClientLibrary
{
    /// <summary>
    /// Worker library for Common Runtime for Applications (CRA)
    /// </summary>
    public class CRAWorker
    {
        CRAClientLibrary _craClient;

        string _workerinstanceName;
        string _address;
        int _port;

        // Azure storage clients
        string _storageConnectionString;
        CloudStorageAccount _storageAccount;
        CloudBlobClient _blobClient;
        CloudTableClient _tableClient;
        CloudTable _workerInstanceTable;
        CloudTable _connectionTable;

        // Timer updateTimer
        ConcurrentDictionary<string, IProcess> _localProcessTable = new ConcurrentDictionary<string, IProcess>();
        ConcurrentDictionary<string, CancellationTokenSource> inConnections = new ConcurrentDictionary<string, CancellationTokenSource>();
        ConcurrentDictionary<string, CancellationTokenSource> outConnections = new ConcurrentDictionary<string, CancellationTokenSource>();
        
        /// <summary>
        /// Instance name
        /// </summary>
        public string InstanceName {  get { return _workerinstanceName;  } }

        /// <summary>
        /// Define a new worker instance of Common Runtime for Applications (CRA)
        /// </summary>
        /// <param name="workerInstanceName">Name of the worker instance</param>
        /// <param name="address">IP address</param>
        /// <param name="port">Port</param>
        /// <param name="storageConnectionString">Storage account to store metadata</param>
        public CRAWorker(string workerInstanceName, string address, int port, string storageConnectionString)
        {
            _craClient = new CRAClientLibrary(storageConnectionString, this);

            _workerinstanceName = workerInstanceName;
            _address = address;
            _port = port;

            _storageConnectionString = storageConnectionString;
            _storageAccount = CloudStorageAccount.Parse(_storageConnectionString);
            _blobClient = _storageAccount.CreateCloudBlobClient();
            _tableClient = _storageAccount.CreateCloudTableClient();
            _workerInstanceTable = CreateTableIfNotExists("processtableforcra");
            _connectionTable = CreateTableIfNotExists("connectiontableforcra");
        }

        /// <summary>
        /// Start the CRA worker. This method does not return.
        /// </summary>
        public void Start()
        {
            Debug.WriteLine("Registering instance with CRA");
            // Update process table
            _craClient.RegisterInstance(_workerinstanceName, _address, _port);

            // Restore processes on machine
            RestoreProcessesAndConnections();

            // Then start server. This ensures that others can establish 
            // connections to local processes at this point.
            Thread serverThread = new Thread(StartServer);
            serverThread.Start();

            // Wait for server to complete execution
            serverThread.Join();
        }


        private void StartServer()
        {
            var server = new TcpListener(IPAddress.Parse(_address), _port);

            // Start listening for client requests.
            server.Start();

            while (true)
            {
                Debug.WriteLine("Waiting for a connection... ");
                TcpClient client = server.AcceptTcpClient();
                client.NoDelay = true;

                Debug.WriteLine("Connected!");

                // Get a stream object for reading and writing
                NetworkStream stream = client.GetStream();

                CRATaskMessageType message = (CRATaskMessageType)stream.ReadInt32();

                switch (message)
                {
                    case CRATaskMessageType.LOAD_PROCESS:
                        Task.Run(() => LoadProcess(stream));
                        break;

                    case CRATaskMessageType.CONNECT_PROCESS_INITIATOR:
                        Task.Run(() => ConnectProcess_Initiator(stream, false));
                        break;

                    case CRATaskMessageType.CONNECT_PROCESS_RECEIVER:
                        Task.Run(() => ConnectProcess_Receiver(stream, false));
                        break;

                    case CRATaskMessageType.CONNECT_PROCESS_INITIATOR_REVERSE:
                        Task.Run(() => ConnectProcess_Initiator(stream, true));
                        break;

                    case CRATaskMessageType.CONNECT_PROCESS_RECEIVER_REVERSE:
                        Task.Run(() => ConnectProcess_Receiver(stream, true));
                        break;

                    default:
                        Console.WriteLine("Unknown message type: " + message);
                        break;
                }
            }
        }

        private void LoadProcess(object streamObject)
        {
            var stream = (Stream)streamObject;

            string processName = Encoding.UTF8.GetString(stream.ReadByteArray());
            string processDefinition = Encoding.UTF8.GetString(stream.ReadByteArray());
            string processParam = Encoding.UTF8.GetString(stream.ReadByteArray());

            _craClient
                .LoadProcessAsync(processName, processDefinition, processParam, _workerinstanceName, _localProcessTable)
                .Wait();

            stream.WriteInt32(0);
            stream.Close();
        }

        private void ConnectProcess_Initiator(object streamObject, bool reverse = false)
        {
            var stream = (Stream)streamObject;

            string fromProcessName = Encoding.UTF8.GetString(stream.ReadByteArray());
            string fromProcessOutput = Encoding.UTF8.GetString(stream.ReadByteArray());
            string toProcessName = Encoding.UTF8.GetString(stream.ReadByteArray());
            string toProcessInput = Encoding.UTF8.GetString(stream.ReadByteArray());

            Debug.WriteLine("Processing request to initiate connection");

            if (!reverse)
            {
                if (!_localProcessTable.ContainsKey(fromProcessName))
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessNotFound);
                    stream.Close();
                    return;
                }

                if (!_localProcessTable[fromProcessName].OutputEndpoints.ContainsKey(fromProcessOutput) &&
                    !_localProcessTable[fromProcessName].AsyncOutputEndpoints.ContainsKey(fromProcessOutput)
                    )
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessInputNotFound);
                    stream.Close();
                    return;
                }
            }
            else
            {
                if (!_localProcessTable.ContainsKey(toProcessName))
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessNotFound);
                    stream.Close();
                    return;
                }

                if (!_localProcessTable[toProcessName].InputEndpoints.ContainsKey(toProcessInput) &&
                    !_localProcessTable[toProcessName].AsyncInputEndpoints.ContainsKey(toProcessInput)
                    )
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessInputNotFound);
                    stream.Close();
                    return;
                }
            }


            var result = Connect_InitiatorSide(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse);

            stream.WriteInt32((int)result);
            stream.Close();
        }

        internal CRAErrorCode Connect_InitiatorSide(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput, bool reverse, bool killIfExists = true, bool killRemote = true)
        {
            ProcessTable row;

            try
            {
                // Need to get the latest address & port
                row = reverse ? ProcessTable.GetRowForProcess(_workerInstanceTable, fromProcessName)
                : ProcessTable.GetRowForProcess(_workerInstanceTable, toProcessName);
            }
            catch
            {
                return CRAErrorCode.ActiveProcessNotFound;
            }

            // If from and to processes are on the same (this) instance,
            // we can convert a "reverse" connection into a normal connection
            if (reverse && (row.InstanceName == InstanceName))
                reverse = false;


            CancellationTokenSource oldSource;
            var conn = reverse ? inConnections : outConnections;

            if (conn.TryGetValue(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput,
                out oldSource))
            {
                if (killIfExists)
                {
                    Debug.WriteLine("Deleting prior connection - it will automatically reconnect");
                    oldSource.Cancel();
                }
                return CRAErrorCode.Success;
            }


            if (TryFusedConnect(row.InstanceName, fromProcessName, fromProcessOutput, toProcessName, toProcessInput))
            {
                return CRAErrorCode.Success;
            }

            // Re-check the connection table as someone may have successfully
            // created a fused connection
            if (conn.TryGetValue(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput,
                out oldSource))
            {
                if (killIfExists)
                {
                    Debug.WriteLine("Deleting prior connection - it will automatically reconnect");
                    oldSource.Cancel();
                }
                return CRAErrorCode.Success;
            }

            // Send request to CRA instance
            TcpClient client = null;
            NetworkStream ns = null;
            try
            {
                var _row = ProcessTable.GetRowForInstanceProcess(_workerInstanceTable, row.InstanceName, "");
                client = new TcpClient(_row.Address, _row.Port);
                client.NoDelay = true;

                ns = client.GetStream();
            }
            catch
            {
                return CRAErrorCode.ConnectionEstablishFailed;
            }
            
            if (!reverse)
                ns.WriteInt32((int)CRATaskMessageType.CONNECT_PROCESS_RECEIVER);
            else
                ns.WriteInt32((int)CRATaskMessageType.CONNECT_PROCESS_RECEIVER_REVERSE);

            ns.WriteByteArray(Encoding.UTF8.GetBytes(fromProcessName));
            ns.WriteByteArray(Encoding.UTF8.GetBytes(fromProcessOutput));
            ns.WriteByteArray(Encoding.UTF8.GetBytes(toProcessName));
            ns.WriteByteArray(Encoding.UTF8.GetBytes(toProcessInput));
            ns.WriteInt32(killRemote ? 1 : 0);
            CRAErrorCode result = (CRAErrorCode) ns.ReadInt32();

            if (result != 0)
            {
                Debug.WriteLine("Client received error code: " + result);
            }
            else
            {
                CancellationTokenSource source = new CancellationTokenSource();

                if (!reverse)
                {
                    if (outConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                    {
                        Task.Run(() => 
                            EgressToStream(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse, ns, source));
                    }
                    else
                    {
                        source.Dispose();
                        ns.Close();
                        Console.WriteLine("Race adding connection - deleting outgoing stream");
                        return CRAErrorCode.ConnectionAdditionRace;
                    }
                }
                else
                {
                    if (inConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                    {
                        Task.Run(() => IngressFromStream
                        (fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse, ns, source));
                    }
                    else
                    {
                        source.Dispose();
                        ns.Close();
                        Debug.WriteLine("Race adding connection - deleting outgoing stream");
                        return CRAErrorCode.ConnectionAdditionRace;
                    }
                }
            }
            return result;
        }

        private bool TryFusedConnect(string otherInstance, string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput)
        {
            if (otherInstance != InstanceName)
                return false;


            CancellationTokenSource source = new CancellationTokenSource();

            if (_localProcessTable[fromProcessName].OutputEndpoints.ContainsKey(fromProcessOutput) &&
                _localProcessTable[toProcessName].InputEndpoints.ContainsKey(toProcessInput))
            {
                var fromProcess = _localProcessTable[fromProcessName].OutputEndpoints[fromProcessOutput] as IFusableProcessOutputEndpoint;
                var toProcess = _localProcessTable[toProcessName].InputEndpoints[toProcessInput] as IProcessInputEndpoint;

                if (fromProcess != null && toProcess != null && fromProcess.CanFuseWith(toProcess, toProcessName, toProcessInput))
                {
                    if (outConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                    {
                        Task.Run(() => EgressToProcessInput(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, source));
                        return true;
                    }
                    else
                        return false;
                }
                else
                    return false;
            }
            else if (_localProcessTable[fromProcessName].AsyncOutputEndpoints.ContainsKey(fromProcessOutput) &&
                _localProcessTable[toProcessName].AsyncInputEndpoints.ContainsKey(toProcessInput))
            {
                var fromProcess = _localProcessTable[fromProcessName].AsyncOutputEndpoints[fromProcessOutput] as IAsyncFusableProcessOutputEndpoint;
                var toProcess = _localProcessTable[toProcessName].AsyncInputEndpoints[toProcessInput] as IAsyncProcessInputEndpoint;

                if (fromProcess != null && toProcess != null && fromProcess.CanFuseWith(toProcess, toProcessName, toProcessInput))
                {
                    if (outConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                    {
                        Task.Run(() => EgressToProcessInput(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, source));
                        return true;
                    }
                    else
                        return false;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private async Task EgressToStream(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput,
            bool reverse, Stream ns, CancellationTokenSource source)
        {
            try
            {
                if (_localProcessTable[fromProcessName].OutputEndpoints.ContainsKey(fromProcessOutput))
                {
                    await
                        Task.Run(() =>
                            _localProcessTable[fromProcessName].OutputEndpoints[fromProcessOutput]
                                .ToStream(ns, toProcessName, toProcessInput, source.Token), source.Token);
                }
                else if (_localProcessTable[fromProcessName].AsyncOutputEndpoints.ContainsKey(fromProcessOutput))
                {
                    await _localProcessTable[fromProcessName].AsyncOutputEndpoints[fromProcessOutput].ToStreamAsync(ns, toProcessName, toProcessInput, source.Token);
                }
                else
                {
                    Debug.WriteLine("Unable to find output endpoint (on from side)");
                    return;
                }

                CancellationTokenSource oldSource;
                if (outConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out oldSource))
                {
                    oldSource.Dispose();
                    ns.Dispose();
                    _craClient.Disconnect(fromProcessName, fromProcessOutput, toProcessName, toProcessInput);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception (" + e.ToString() + ") in outgoing stream - reconnecting");
                CancellationTokenSource oldSource;
                if (outConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out oldSource))
                {
                    oldSource.Dispose();
                }
                else
                {
                    Debug.WriteLine("Unexpected: caught exception in ToStream but entry absent in outConnections");
                }

                // Retry following while connection not in list
                RetryRestoreConnection(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, false);
            }
        }

        private async Task EgressToProcessInput(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput, 
            CancellationTokenSource source)
        {
            try
            {
                if (_localProcessTable[fromProcessName].OutputEndpoints.ContainsKey(fromProcessOutput))
                {
                    var fromProcess = _localProcessTable[fromProcessName].OutputEndpoints[fromProcessOutput] as IFusableProcessOutputEndpoint;
                    var toProcess = _localProcessTable[toProcessName].InputEndpoints[toProcessInput] as IProcessInputEndpoint;

                    if (fromProcess != null && toProcess != null && fromProcess.CanFuseWith(toProcess, toProcessName, toProcessInput))
                    {
                        await
                            Task.Run(() => fromProcess.ToInput(toProcess, toProcessName, toProcessInput, source.Token));
                    }
                    else
                    {
                        Debug.WriteLine("Unable to create fused connection");
                        return;
                    }
                }
                else if (_localProcessTable[fromProcessName].AsyncOutputEndpoints.ContainsKey(fromProcessOutput))
                {
                    var fromProcess = _localProcessTable[fromProcessName].AsyncOutputEndpoints[fromProcessOutput] as IAsyncFusableProcessOutputEndpoint;
                    var toProcess = _localProcessTable[toProcessName].AsyncInputEndpoints[toProcessInput] as IAsyncProcessInputEndpoint;

                    if (fromProcess != null && toProcess != null && fromProcess.CanFuseWith(toProcess, toProcessName, toProcessInput))
                    {
                        await fromProcess.ToInputAsync(toProcess, fromProcessName, fromProcessOutput, source.Token);
                    }
                    else
                    {
                        Debug.WriteLine("Unable to create fused connection");
                        return;
                    }
                }
                else
                {
                    Debug.WriteLine("Unable to create fused connection");
                    return;
                }

                CancellationTokenSource oldSource;
                if (outConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out oldSource))
                {
                    oldSource.Dispose();
                    _craClient.Disconnect(fromProcessName, fromProcessOutput, toProcessName, toProcessInput);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception (" + e.ToString() + ") in outgoing stream - reconnecting");
                CancellationTokenSource oldSource;
                if (outConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out oldSource))
                {
                    oldSource.Dispose();
                }
                else
                {
                    Debug.WriteLine("Unexpected: caught exception in ToStream but entry absent in outConnections");
                }

                // Retry following while connection not in list
                RetryRestoreConnection(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, false);
            }
        }

        private void ConnectProcess_Receiver(object streamObject, bool reverse = false)
        {
            var stream = (Stream)streamObject;

            string fromProcessName = Encoding.UTF8.GetString(stream.ReadByteArray());
            string fromProcessOutput = Encoding.UTF8.GetString(stream.ReadByteArray());
            string toProcessName = Encoding.UTF8.GetString(stream.ReadByteArray());
            string toProcessInput = Encoding.UTF8.GetString(stream.ReadByteArray());
            bool killIfExists = stream.ReadInt32() == 1 ? true : false;

            if (!reverse)
            {
                if (!_localProcessTable.ContainsKey(toProcessName))
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessNotFound);
                    stream.Close();
                    return;
                }

                if (!_localProcessTable[toProcessName].InputEndpoints.ContainsKey(toProcessInput) &&
                    !_localProcessTable[toProcessName].AsyncInputEndpoints.ContainsKey(toProcessInput)
                    )
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessInputNotFound);
                    stream.Close();
                    return;
                }
            }
            else
            {
                if (!_localProcessTable.ContainsKey(fromProcessName))
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessNotFound);
                    stream.Close();
                    return;
                }

                if (!_localProcessTable[fromProcessName].OutputEndpoints.ContainsKey(fromProcessOutput) &&
                    !_localProcessTable[fromProcessName].AsyncOutputEndpoints.ContainsKey(fromProcessOutput)
                    )
                {
                    stream.WriteInt32((int)CRAErrorCode.ProcessInputNotFound);
                    stream.Close();
                    return;
                }
            }

            var result = Connect_ReceiverSide(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, stream, reverse, killIfExists);

            // Do not close and dispose stream because it is being reused for data
            if (result != 0)
            {
                stream.Close();
            }
        }

        private int Connect_ReceiverSide(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput, Stream stream, bool reverse, bool killIfExists = true)
        {
            CancellationTokenSource oldSource;

            var conn = reverse ? outConnections : inConnections;

            if (conn.TryGetValue(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, 
                out oldSource))
            {
                if (killIfExists)
                {
                    Debug.WriteLine("Deleting prior connection - it will automatically reconnect");
                    oldSource.Cancel();
                }
                else
                {
                    Debug.WriteLine("There exists prior connection - not killing");
                }
                stream.WriteInt32((int)CRAErrorCode.ServerRecovering);
                return (int)CRAErrorCode.ServerRecovering;
            }
            else
            {
                stream.WriteInt32(0);
            }

            CancellationTokenSource source = new CancellationTokenSource();

            if (!reverse)
            {
                if (inConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                {
                    Task.Run(() =>
                        IngressFromStream(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse, stream, source));
                    return (int)CRAErrorCode.Success;
                }
                else
                {
                    source.Dispose();
                    stream.Close();
                    Debug.WriteLine("Race adding connection - deleting incoming stream");
                    return (int)CRAErrorCode.ConnectionAdditionRace;
                }
            }
            else
            {
                if (outConnections.TryAdd(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, source))
                {
                    Task.Run(() =>
                        EgressToStream(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse, stream, source));
                    return (int)CRAErrorCode.Success;
                }
                else
                {
                    source.Dispose();
                    stream.Close();
                    Debug.WriteLine("Race adding connection - deleting incoming stream");
                    return (int)CRAErrorCode.ConnectionAdditionRace;
                }
            }
        }

        private async Task IngressFromStream(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput, bool reverse, Stream ns, CancellationTokenSource source)
        {
            try
            {
                if (_localProcessTable[toProcessName].InputEndpoints.ContainsKey(toProcessInput))
                {
                    await Task.Run(
                        () => _localProcessTable[toProcessName].InputEndpoints[toProcessInput]
                        .FromStream(ns, fromProcessName, fromProcessOutput, source.Token), source.Token);
                }
                else if (_localProcessTable[toProcessName].AsyncInputEndpoints.ContainsKey(toProcessInput))
                {
                    await _localProcessTable[toProcessName].AsyncInputEndpoints[toProcessInput].FromStreamAsync(ns, fromProcessName, fromProcessOutput, source.Token);
                }
                else
                {
                    Debug.WriteLine("Unable to find input endpoint (on to side)");
                    return;
                }

                // Completed FromStream successfully
                CancellationTokenSource oldSource;
                if (outConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out oldSource))
                {
                    oldSource.Dispose();
                    ns.Dispose();
                    _craClient.Disconnect(fromProcessName, fromProcessOutput, toProcessName, toProcessInput);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception (" + e.ToString() + ") in incoming stream - reconnecting");
                CancellationTokenSource tokenSource;
                if (inConnections.TryRemove(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput, out tokenSource))
                {
                    tokenSource.Dispose();
                }
                else
                {
                    Debug.WriteLine("Unexpected: caught exception in FromStream but entry absent in inConnections");
                }

                RetryRestoreConnection(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, true);
            }
        }

        private void RestoreProcessesAndConnections()
        {
            var rows = ProcessTable.GetAllRowsForInstance(_workerInstanceTable, _workerinstanceName);

            foreach (var _row in rows)
            {
                if (string.IsNullOrEmpty(_row.ProcessName)) continue;
                if (_row.IsActive)
                {
                    _craClient.DeactivateProcess(_row.ProcessName, _workerinstanceName);
                }
                RestoreProcessAndConnections(_row);
            }
        }

        private async void RestoreProcessAndConnections(ProcessTable _row)
        {
            await _craClient.LoadProcessAsync(_row.ProcessName, _row.ProcessDefinition, _row.ProcessParameter, _workerinstanceName, _localProcessTable);
            RestoreConnections(_row);
        }

        private void RestoreConnections(ProcessTable _row)
        {
            // Decide what to do if connection creation fails
            var outRows = ConnectionTable.GetAllConnectionsFromProcess(_connectionTable, _row.ProcessName).ToList();
            foreach (var row in outRows)
            {
                Task.Run(() => RetryRestoreConnection(row.FromProcess, row.FromEndpoint, row.ToProcess, row.ToEndpoint, false));
            }

            var inRows = ConnectionTable.GetAllConnectionsToProcess(_connectionTable, _row.ProcessName).ToList();
            foreach (var row in inRows)
            {
                Task.Run(() => RetryRestoreConnection(row.FromProcess, row.FromEndpoint, row.ToProcess, row.ToEndpoint, true));
            }
        }

        private void RetryRestoreConnection(string fromProcessName, string fromProcessOutput, string toProcessName, string toProcessInput, bool reverse)
        {
            var conn = reverse ? inConnections : outConnections;

            bool killRemote = false;
            while (!conn.ContainsKey(fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput))
            {
                if (!ConnectionTable.ContainsConnection(_connectionTable, fromProcessName, fromProcessOutput, toProcessName, toProcessInput))
                    break;

                Debug.WriteLine("Connecting " + fromProcessName + ":" + fromProcessOutput + ":" + toProcessName + ":" + toProcessInput);
                Debug.WriteLine("Connecting with killRemote set to " + (killRemote ? "true" : "false"));

                var result = Connect_InitiatorSide(fromProcessName, fromProcessOutput, toProcessName, toProcessInput, reverse, false, killRemote);

                if (result != 0)
                {
                    if (result == CRAErrorCode.ServerRecovering)
                        killRemote = true;
                    Thread.Sleep(5000);
                }
                else
                    break;
            }
        }

        private CloudTable CreateTableIfNotExists(string tableName)
        {
            CloudTable table = _tableClient.GetTableReference(tableName);
            try
            {
                Debug.WriteLine("Creating table " + tableName);
                table.CreateIfNotExists();
            }
            catch { }

            return table;
        }
    }
}
