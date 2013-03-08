﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using MegaApi.Cryptography;
using MegaApi.DataTypes;
using MegaApi.Utility;
using MegaApi.Comms.Requests;
using System.Linq;

namespace MegaApi.Comms.Transfers
{
    internal class MegaUploader : TransferController
    {
        public MegaUser User { get; set; }

        public MegaUploader(Transport transport, string tempFolder = null, int maxConnections = 4, int maxThreads = 4)
            : base(transport, tempFolder, maxConnections, maxThreads) { }

        public UploadHandle UploadFile(string filename, string targetNode, string uploadUrl, string tempPath = null)
        {
            var handle = new UploadHandle(filename, tempPath);
            handle.UploadUrl = uploadUrl;
            handle.UploadTargetNode = targetNode;
            handle.Node = new MegaNode
            {
                Type = MegaNodeType.Dummy,
                Attributes = new NodeAttributes { Name = Path.GetFileName(filename) }
            };

            handle.UploadKey = Crypto.RandomKey(24);
            var ul_aes_key = new byte[16];
            Array.Copy(handle.UploadKey, ul_aes_key, 16);
            handle.AesAlg = Crypto.CreateAes(ul_aes_key);
            handle.Nonce = new byte[8];
            Array.Copy(handle.UploadKey, 16, handle.Nonce, 0, 8);

            EnqueueCrypt(handle.Chunks);

            return handle;

        }

        void PrepareNodeKeys(UploadHandle handle)
        {
            handle.NodeKeys = new byte[32];
            // oook:
            Array.Copy(handle.UploadKey.XorWith(0, handle.UploadKey, 16, 8), handle.NodeKeys, 8);
            Array.Copy(handle.UploadKey, 16, handle.NodeKeys, 16, 8);
            Array.Copy(handle.Mac.XorWith(0, handle.Mac, 4, 4), 0, handle.NodeKeys, 24, 4);
            Array.Copy(handle.Mac.XorWith(8, handle.Mac, 12, 4), 8, handle.NodeKeys, 28, 4);
            // already xored:
            Array.Copy(handle.UploadKey.XorWith(8, handle.Mac, 0, 4), 8, handle.NodeKeys, 8, 4);
            Array.Copy(handle.UploadKey.XorWith(12, handle.Mac, 8, 4), 12, handle.NodeKeys, 12, 4);
        }

        protected override void CryptChunk(MegaChunk chunk)
        {
            byte[] data = new byte[chunk.Size];  
            var hndl = chunk.Handle;
            try
            {
                hndl.Stream.Seek(chunk.Offset, SeekOrigin.Begin);
                hndl.Stream.Read(data, 0, data.Length);
            }
            catch
            {
                hndl.CancelTransfer();
                hndl.Error = MegaApiError.ELOCAL;
                return;
            }
            chunk.Mac = Crypto.EncryptCtr(chunk.Handle.AesAlg, data, chunk.Handle.Nonce, chunk.Offset);
            chunk.Data = data;
            EnqueueTransfer(new List<MegaChunk>{chunk});
        }

        protected override void TransferChunk(MegaChunk chunk, Action transferCompleteFn)
        {
            if (chunk.Handle.SkipChunks) { transferCompleteFn(); return; }
            var wc = new WebClient();
            wc.Proxy = transport.Proxy;
            chunk.Handle.OnStartedTransfer(wc);
            wc.UploadDataCompleted += (s, e) =>
            {
                chunk.Handle.OnEndedTransfer(wc);
                transferCompleteFn();
                if (e.Cancelled) { return; }
                if (e.Error != null)
                {
                    chunk.Handle.OnTransferredBytes(0 - chunk.transferredBytes);
                    EnqueueTransfer(new List<MegaChunk>{chunk}, true);
                }
                else { OnUploadedChunk(chunk, e); }
            };
            wc.UploadProgressChanged += (s, e) => OnUploadedBytes(chunk, e.BytesSent);
            var url = String.Format(((UploadHandle)chunk.Handle).UploadUrl + "/{0}", chunk.Offset);
            wc.UploadDataAsync(new Uri(url), chunk.Data);

        }

        private void OnUploadedChunk(MegaChunk chunk, UploadDataCompletedEventArgs e)
        {
            chunk.ClearData();
            chunk.Handle.chunksProcessed++;
            if (chunk.Handle.chunksProcessed == chunk.Handle.Chunks.Count)
            {
                var uploadHandle = Encoding.UTF8.GetString(e.Result);
                FinishFile((UploadHandle)chunk.Handle, uploadHandle);
            }
        }

        private void FinishFile(UploadHandle handle, string uploadHandle)
        {
            handle.Progress = 100;
            handle.Mac = new byte[16];
            foreach (var chunk in handle.Chunks)
            {
                handle.Mac = handle.Mac.Xor(chunk.Mac);
                handle.Mac = Crypto.Encrypt(handle.AesAlg, handle.Mac);
            }
            PrepareNodeKeys(handle);
            var node = new MegaNode
            {
                Id = uploadHandle,
                ParentId = handle.UploadTargetNode,
                Type = MegaNodeType.File,
                Attributes = new NodeAttributes { Name = Path.GetFileName(handle.LocalFilename) },
                NodeKey = new NodeKeys(handle.NodeKeys, User)
            };
            var r = new MRequestCompleteUpload<MResponseCompleteUpload>(User, node);
            r.Success += (s, a) =>
            {
                handle.Node = a.NewNode.FirstOrDefault();
                handle.Status = TransferHandleStatus.Success;
            };
            r.Error += (s, a) =>
            {
                handle.Error = a.Error;
                handle.Status = TransferHandleStatus.Error;
            };
            transport.EnqueueRequest(r);
        }

        

        




    }
}
