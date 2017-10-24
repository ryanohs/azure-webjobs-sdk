﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Converters;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Host.Storage;
using Microsoft.Azure.WebJobs.Host.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Azure.WebJobs.Host.Config;
using System.Threading;
using System.Collections;
using Microsoft.Azure.WebJobs.Host.Blobs.Listeners;
using Microsoft.Azure.WebJobs.Host.Protocols;

namespace Microsoft.Azure.WebJobs.Host.Blobs.Bindings
{
    internal class BlobExtension : IExtensionConfigProvider,
        IAsyncConverter<BlobAttribute, Stream>,
        IAsyncConverter<BlobAttribute, CloudBlobStream>,
        IAsyncConverter<BlobAttribute, CloudBlockBlob>,
        IAsyncConverter<BlobAttribute, CloudPageBlob>,
        IAsyncConverter<BlobAttribute, CloudAppendBlob>,
        IAsyncConverter<BlobAttribute, ICloudBlob>,
        IAsyncConverter<BlobAttribute, CloudBlobContainer>,
        IAsyncConverter<BlobAttribute, CloudBlobDirectory>,
        IAsyncConverter<BlobAttribute, BlobExtension.MultiBlobContext>

    {
        private IStorageAccountProvider _accountProvider;
        private IContextGetter<IBlobWrittenWatcher> _blobWrittenWatcherGetter;
        private INameResolver _nameResolver;


        #region Container rules
        async Task<CloudBlobContainer> IAsyncConverter<BlobAttribute, CloudBlobContainer>.ConvertAsync(
            BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var container = await GetContainerAsync(blobAttribute, cancellationToken);
            return container.SdkObject;
        }

        // Write-only rule. 
        async Task<CloudBlobDirectory> IAsyncConverter<BlobAttribute, CloudBlobDirectory>.ConvertAsync(
            BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            IStorageBlobClient client = await GetClientAsync(blobAttribute, cancellationToken);

            BlobPath boundPath = BlobPath.ParseAndValidate(blobAttribute.BlobPath, isContainerBinding: false);

            IStorageBlobContainer container = client.GetContainerReference(boundPath.ContainerName);

            CloudBlobDirectory directory = container.SdkObject.GetDirectoryReference(
                boundPath.BlobName);

            return directory;
        }

        #endregion

        #region CloudBlob rules 

        // Write-only rule. 
        async Task<CloudBlobStream> IAsyncConverter<BlobAttribute, CloudBlobStream>.ConvertAsync(
            BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            // $$$ Fix cast. 
            return (CloudBlobStream)await CreateStreamAsync(blobAttribute, cancellationToken);
        }


        async Task<CloudBlockBlob> IAsyncConverter<BlobAttribute, CloudBlockBlob>.ConvertAsync(
            BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var blob = await GetBlobAsync(blobAttribute, cancellationToken, typeof(CloudBlockBlob));
            return (CloudBlockBlob)(blob.SdkObject);
        }

        async Task<CloudPageBlob> IAsyncConverter<BlobAttribute, CloudPageBlob>.ConvertAsync(
    BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var blob = await GetBlobAsync(blobAttribute, cancellationToken, typeof(CloudPageBlob));
            return (CloudPageBlob)(blob.SdkObject);
        }

        async Task<CloudAppendBlob> IAsyncConverter<BlobAttribute, CloudAppendBlob>.ConvertAsync(
    BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var blob = await GetBlobAsync(blobAttribute, cancellationToken, typeof(CloudAppendBlob));
            return (CloudAppendBlob)(blob.SdkObject);
        }

        async Task<ICloudBlob> IAsyncConverter<BlobAttribute, ICloudBlob>.ConvertAsync(
    BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var blob = await GetBlobAsync(blobAttribute, cancellationToken, typeof(ICloudBlob));
            return (ICloudBlob)(blob.SdkObject);
        }
        #endregion
        
        #region Support for binding to Multiple blobs 
        // Open type matching types that can bind to an IEnumerable<T> blob collection. 
        class MultiBlobType : OpenType
        {
            private static readonly Type[] _types = new Type[]
            {
                typeof(ICloudBlob),
                typeof(CloudBlockBlob),
                typeof(CloudPageBlob),
                typeof(CloudAppendBlob),
                typeof(TextReader),
                typeof(Stream),
                typeof(string)
            };

            public override bool IsMatch(Type type)
            {
                bool match = _types.Contains(type);
                return match;
            }
        }

        // Converter to produce an IEnumerable<T> for binding to multiple blobs. 
        // T must have been matched by MultiBlobType        
        class MultiBlobConverer<T> : IAsyncConverter<MultiBlobContext, IEnumerable<T>>
        {
            public MultiBlobConverer(BlobExtension parent)
            {
            }
            public async Task<IEnumerable<T>> ConvertAsync(MultiBlobContext context, CancellationToken cancellationToken)
            {
                // Query the blob container using the blob prefix (if specified)
                // Note that we're explicitly using useFlatBlobListing=true to collapse
                // sub directories. If users want to bind to a sub directory, they can
                // bind to CloudBlobDirectory.
                string prefix = context.Prefix;
                var container = context.Container;
                IEnumerable<IStorageListBlobItem> blobItems = await container.ListBlobsAsync(prefix, true, cancellationToken);

                // create an IEnumerable<T> of the correct type, performing any
                // required conversions on the blobs                
                var list = await ConvertBlobs(blobItems);
                return list;
            }

            // $$$ This whole block is a legacy from the CloudBlobEnumerableArgumentBinding.
            // Would be nice to share them with the individual blob converters, although there are subtle differences.
            private static async Task<IEnumerable<T>> ConvertBlobs(IEnumerable<IStorageListBlobItem> blobItems)
            {
                var list = new List<T>();

                foreach (var blobItem in blobItems)
                {
                    var converted = await ConvertBlob(((IStorageBlob)blobItem).SdkObject);
                    list.Add(converted);
                }

                return list;
            }

            private static async Task<T> ConvertBlob(ICloudBlob blob)
            {
                object converted = blob;
                var targetType = typeof(T);

                if (targetType == typeof(Stream))
                {
                    converted = await blob.OpenReadAsync(null, null, null);
                }
                else if (targetType == typeof(TextReader))
                {
                    Stream stream = await blob.OpenReadAsync(null, null, null);
                    converted = new StreamReader(stream);
                }
                else if (targetType == typeof(string))
                {
                    Stream stream = await blob.OpenReadAsync(null, null, null);
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        converted = await reader.ReadToEndAsync();
                    }
                }

                return (T)converted;
            }
        }

        // Internal context object to aide in binding to  multiple blobs. 
        private class MultiBlobContext
        {
            public string Prefix;
            public IStorageBlobContainer Container;
        }

        // Initial rule that captures the muti-blob context.
        // Then a converter morphs this to the user type
        async Task<MultiBlobContext> IAsyncConverter<BlobAttribute, MultiBlobContext>.ConvertAsync(BlobAttribute attr, CancellationToken cancellationToken)
        {
            var path = BlobPath.ParseAndValidate(attr.BlobPath, isContainerBinding: true);

            return new MultiBlobContext
            {
                Prefix = path.BlobName,
                Container = await this.GetContainerAsync(attr, cancellationToken)
            };
        }
        #endregion

        public async Task<Stream> ConvertAsync(BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            return await CreateStreamAsync(blobAttribute, cancellationToken);
        }

        private async Task<Stream> CreateStreamAsync(BlobAttribute blobAttribute, CancellationToken cancellationToken)
        {
            var fbc = new FunctionBindingContext(Guid.NewGuid(), cancellationToken, null);
            var vbc = new ValueBindingContext(fbc, cancellationToken);
            // $$$ Stamp with FunctionInstaceId, for causality 

            var blob = await GetBlobAsync(blobAttribute, cancellationToken);

            switch (blobAttribute.Access)
            {
                case FileAccess.Read:
                    var readStream = await ReadBlobArgumentBinding.TryBindStreamAsync(blob, vbc);
                    return readStream;

                case FileAccess.Write:
                    var writeStream = await WriteBlobArgumentBinding.BindStreamAsync(blob,
                    vbc, _blobWrittenWatcherGetter.Value);
                    return writeStream;

                default:
                    throw new InvalidOperationException("Cannot bind blob to Stream using FileAccess ReadWrite.");
            }
        }

        private async Task<IStorageBlobClient> GetClientAsync(
         BlobAttribute blobAttribute,
         CancellationToken cancellationToken)
        {
            IStorageAccount account = await _accountProvider.GetStorageAccountAsync(blobAttribute, cancellationToken, _nameResolver);
            IStorageBlobClient client = account.CreateBlobClient();
            return client;
        }

        private async Task<IStorageBlobContainer> GetContainerAsync(
            BlobAttribute blobAttribute,
            CancellationToken cancellationToken)
        {
            IStorageBlobClient client = await GetClientAsync(blobAttribute, cancellationToken);

            BlobPath boundPath = BlobPath.ParseAndValidate(blobAttribute.BlobPath, isContainerBinding: true);

            IStorageBlobContainer container = client.GetContainerReference(boundPath.ContainerName);
            return container;
        }

        private async Task<IStorageBlob> GetBlobAsync(
        BlobAttribute blobAttribute,
        CancellationToken cancellationToken,
        Type requestedType = null)
        {
            IStorageBlobClient client = await GetClientAsync(blobAttribute, cancellationToken);

            // $$$ This handles URL; but we could make it fully handle any SAS  URL, and skip Connection string. 
            BlobPath boundPath = BlobPath.ParseAndValidate(blobAttribute.BlobPath);

            IStorageBlobContainer container = client.GetContainerReference(boundPath.ContainerName);

            if (blobAttribute.Access != FileAccess.Read)
            {
                await container.CreateIfNotExistsAsync(cancellationToken);
            }

            IStorageBlob blob = await container.GetBlobReferenceForArgumentTypeAsync(
                boundPath.BlobName, requestedType, cancellationToken);

            return blob;
        }

        public void Initialize(ExtensionConfigContext context)
        {
            _accountProvider = context.Config.GetService<IStorageAccountProvider>();

            // $$$ Per-host 
            _blobWrittenWatcherGetter = context.PerHostServices.GetService<ContextAccessor<IBlobWrittenWatcher>>();
            _nameResolver = context.Config.NameResolver;

            var rule = context.AddBindingRule<BlobAttribute>();

            // Bind to multiple blobs (either via a container; a blob directory, an IEnumerable<T>)
            rule.BindToInput<CloudBlobDirectory>(this);

            rule.BindToInput<CloudBlobContainer>(this);

            rule.BindToInput<MultiBlobContext>(this); // Intermediate private context to capture state
            rule.AddConverter<MultiBlobContext, IEnumerable<MultiBlobType>>(typeof(MultiBlobConverer<>), this);

            // BindToStream will also handle the custom Stream-->T converters.
            rule.SetPostResolveHook(ToBlobDescr).BindToStream(this, FileAccess.ReadWrite); // Precedence, must beat CloudBlobStream

            // Normal blob
            rule.SetPostResolveHook(ToBlobDescr).BindToInput<CloudBlockBlob>(this);
            rule.SetPostResolveHook(ToBlobDescr).BindToInput<CloudPageBlob>(this);
            rule.SetPostResolveHook(ToBlobDescr).BindToInput<CloudAppendBlob>(this);
            rule.SetPostResolveHook(ToBlobDescr).BindToInput<ICloudBlob>(this); // base interface 

            // $$$ Only when Access == FileAccess.Write
            // CloudBlobStream's derived functionality is only relevant to writing. 
            rule.SetPostResolveHook(ToBlobDescr).BindToInput<CloudBlobStream>(this);
        }

        private ParameterDescriptor ToBlobDescr(BlobAttribute attr, ParameterInfo parameter, INameResolver nameResolver)
        {
            // Resolve the connection string to get an account name. 
            var client = Task.Run(() => this.GetClientAsync(attr, CancellationToken.None)).GetAwaiter().GetResult();
            var accountName = client.Credentials.AccountName;

            var resolved = nameResolver.ResolveWholeString(attr.BlobPath);

            string containerName = resolved;
            string blobName= null;
            int split = resolved.IndexOf('/');
            if (split > 0)
            {
                containerName = resolved.Substring(0, split);
                blobName = resolved.Substring(split + 1);
            }

            FileAccess access = FileAccess.ReadWrite;
            if (attr.Access.HasValue)
            {
                access = attr.Access.Value;
            }
            else
            {
                var type = parameter.ParameterType;
                if (type.IsByRef || type == typeof(TextWriter))
                {
                    access = FileAccess.Write;
                }
                if (type == typeof(TextReader) || type == typeof(string) || type == typeof(byte[]))
                {
                    access = FileAccess.Read;
                }
            }

            return new BlobParameterDescriptor
            {
                Name = parameter.Name,
                AccountName = accountName,
                ContainerName = containerName,
                BlobName = blobName,
                Access = access
            };
        }
    }
}