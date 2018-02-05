﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using NuGet.Jobs.Configuration;
using NuGet.Jobs.Validation.PackageSigning.Configuration;
using NuGet.Jobs.Validation.PackageSigning.Messages;
using NuGet.Jobs.Validation.PackageSigning.Storage;
using NuGet.Packaging.Signing;
using NuGet.Services.Configuration;
using NuGet.Services.KeyVault;
using NuGet.Services.ServiceBus;
using NuGet.Services.Storage;
using NuGet.Services.Validation;
using NuGet.Services.Validation.PackageSigning;
using NuGetGallery;

namespace NuGet.Jobs.Validation.PackageSigning.ExtractAndValidateSignature
{
    public class Job : JobBase
    {
        /// <summary>
        /// The maximum number of concurrent connections that can be established to a single server.
        /// </summary>
        private const int MaximumConnectionsPerServer = 64;

        /// <summary>
        /// The argument this job uses to determine the configuration file's path.
        /// </summary>
        private const string ConfigurationArgument = "Configuration";

        private const string GalleryDbConfigurationSectionName = "GalleryDb";
        private const string ValidationDbConfigurationSectionName = "ValidationDb";
        private const string ServiceBusConfigurationSectionName = "ServiceBus";
        private const string CertificateStoreConfigurationSectionName = "CertificateStore";
        private const string PackageDownloadTimeoutName = "PackageDownloadTimeout";

        /// <summary>
        /// The maximum time that a KeyVault secret will be cached for.
        /// </summary>
        private static readonly TimeSpan KeyVaultSecretCachingTimeout = TimeSpan.FromDays(1);

        /// <summary>
        /// The maximum amount of time that graceful shutdown can take before the job will
        /// forcefully end itself.
        /// </summary>
        private static readonly TimeSpan MaxShutdownTime = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The configured service provider, used to instiate the services this job depends on.
        /// </summary>
        private IServiceProvider _serviceProvider;

        public override void Init(IDictionary<string, string> jobArgsDictionary)
        {
            var configurationFilename = JobConfigurationManager.GetArgument(jobArgsDictionary, ConfigurationArgument);

            _serviceProvider = GetServiceProvider(GetConfigurationRoot(configurationFilename));

            ServicePointManager.DefaultConnectionLimit = MaximumConnectionsPerServer;
        }

        public override async Task Run()
        {
            var processor = _serviceProvider.GetRequiredService<ISubscriptionProcessor<SignatureValidationMessage>>();

            processor.Start();

            // Wait a day, and then shutdown this process so that it is restarted.
            await Task.Delay(TimeSpan.FromDays(1));
            
            if (!await processor.ShutdownAsync(MaxShutdownTime))
            {
                Logger.LogWarning(
                    "Failed to gracefully shutdown Service Bus subscription processor. {MessagesInProgress} messages left",
                    processor.NumberOfMessagesInProgress);
            }
        }

        private IConfigurationRoot GetConfigurationRoot(string configurationFilename)
        {
            Logger.LogInformation("Using the {ConfigurationFilename} configuration file", Path.Combine(Environment.CurrentDirectory, configurationFilename));

            var builder = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile(configurationFilename, optional: false, reloadOnChange: true);

            var uninjectedConfiguration = builder.Build();

            var secretReaderFactory = new ConfigurationRootSecretReaderFactory(uninjectedConfiguration);
            var cachingSecretReaderFactory = new CachingSecretReaderFactory(secretReaderFactory, KeyVaultSecretCachingTimeout);
            var secretInjector = cachingSecretReaderFactory.CreateSecretInjector(cachingSecretReaderFactory.CreateSecretReader());

            builder = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddInjectedJsonFile(configurationFilename, secretInjector);

            return builder.Build();
        }

        private void ConfigureJobServices(IServiceCollection services, IConfigurationRoot configurationRoot)
        {
            services.Configure<GalleryDbConfiguration>(configurationRoot.GetSection(GalleryDbConfigurationSectionName));
            services.Configure<ValidationDbConfiguration>(configurationRoot.GetSection(ValidationDbConfigurationSectionName));
            services.Configure<ServiceBusConfiguration>(configurationRoot.GetSection(ServiceBusConfigurationSectionName));
            services.Configure<CertificateStoreConfiguration>(configurationRoot.GetSection(CertificateStoreConfigurationSectionName));

            services.AddTransient<ISubscriptionProcessor<SignatureValidationMessage>, SubscriptionProcessor<SignatureValidationMessage>>();

            services.AddScoped<IValidationEntitiesContext>(p =>
            {
                var config = p.GetRequiredService<IOptionsSnapshot<ValidationDbConfiguration>>().Value;

                return new ValidationEntitiesContext(config.ConnectionString);
            });

            services.AddTransient<IEntityRepository<Certificate>, EntityRepository<Certificate>>();

            services.AddScoped<IEntitiesContext>(p =>
            {
                var config = p.GetRequiredService<IOptionsSnapshot<GalleryDbConfiguration>>().Value;

                return new EntitiesContext(config.ConnectionString, readOnly: true);
            });

            services.AddTransient<ISubscriptionClient>(p =>
            {
                var config = p.GetRequiredService<IOptionsSnapshot<ServiceBusConfiguration>>().Value;

                return new SubscriptionClientWrapper(config.ConnectionString, config.TopicPath, config.SubscriptionName);
            });

            services.AddTransient<ICertificateStore>(p =>
            {
                var config = p.GetRequiredService<IOptionsSnapshot<CertificateStoreConfiguration>>().Value;
                var targetStorageAccount = CloudStorageAccount.Parse(config.DataStorageAccount);

                var storageFactory = new AzureStorageFactory(targetStorageAccount, config.ContainerName, LoggerFactory.CreateLogger<AzureStorage>());
                var storage = storageFactory.Create();

                return new CertificateStore(storage, LoggerFactory.CreateLogger<CertificateStore>());
            });

            services.AddTransient<IBrokeredMessageSerializer<SignatureValidationMessage>, SignatureValidationMessageSerializer>();
            services.AddTransient<IMessageHandler<SignatureValidationMessage>, SignatureValidationMessageHandler>();
            services.AddTransient<IPackageSigningStateService, PackageSigningStateService>();
            services.AddTransient<ISignaturePartsExtractor, SignaturePartsExtractor>();

            services.AddTransient<ISignatureValidator, SignatureValidator>(p => new SignatureValidator(
                p.GetRequiredService<IPackageSigningStateService>(),
                PackageSignatureVerifierFactory.CreateMinimal(),
                PackageSignatureVerifierFactory.CreateFull(),
                p.GetRequiredService<ISignaturePartsExtractor>(),
                p.GetRequiredService<IEntityRepository<Certificate>>(),
                p.GetRequiredService<ILogger<SignatureValidator>>()));

            services.AddSingleton(p =>
            {
                var assembly = Assembly.GetEntryAssembly();
                var assemblyName = assembly.GetName().Name;
                var assemblyVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

                var client = new HttpClient(new WebRequestHandler
                {
                    AllowPipelining = true,
                    AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate),
                });

                client.Timeout = configurationRoot.GetValue<TimeSpan>(PackageDownloadTimeoutName);
                client.DefaultRequestHeaders.Add("user-agent", $"{assemblyName}/{assemblyVersion}");

                return client;
            });
        }

        private IServiceProvider GetServiceProvider(IConfigurationRoot configurationRoot)
        {
            var services = new ServiceCollection();

            ConfigureLibraries(services);
            ConfigureJobServices(services, configurationRoot);

            return CreateProvider(services);
        }

        private void ConfigureLibraries(IServiceCollection services)
        {
            // Use the custom NonCachingOptionsSnapshot so that KeyVault secret injection works properly.
            services.Add(ServiceDescriptor.Scoped(typeof(IOptionsSnapshot<>), typeof(NonCachingOptionsSnapshot<>)));
            services.AddSingleton(LoggerFactory);
            services.AddLogging();
        }

        private static IServiceProvider CreateProvider(IServiceCollection services)
        {
            const string validateSignatureBindingKey = "ValidateSignatureKey";
            var signatureValidationMessageHandlerType = typeof(IMessageHandler<SignatureValidationMessage>);

            var containerBuilder = new ContainerBuilder();

            containerBuilder.Populate(services);

            containerBuilder
                .RegisterType<ValidatorStateService>()
                .WithParameter(
                    (pi, ctx) => pi.ParameterType == typeof(Type),
                    (pi, ctx) => typeof(PackageSigningValidator))
                .As<IValidatorStateService>();

            containerBuilder
                .RegisterType<PackageSigningStateService>()
                .As<IPackageSigningStateService>();

            containerBuilder
                .RegisterType<ScopedMessageHandler<SignatureValidationMessage>>()
                .Keyed<IMessageHandler<SignatureValidationMessage>>(validateSignatureBindingKey);

            containerBuilder
                .RegisterType<SubscriptionProcessor<SignatureValidationMessage>>()
                .WithParameter(
                    (parameter, context) => parameter.ParameterType == signatureValidationMessageHandlerType,
                    (parameter, context) => context.ResolveKeyed(validateSignatureBindingKey, signatureValidationMessageHandlerType))
                .As<ISubscriptionProcessor<SignatureValidationMessage>>();

            return new AutofacServiceProvider(containerBuilder.Build());
        }
    }
}