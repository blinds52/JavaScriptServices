// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.SpaServices.Extensions.Proxy;
using Microsoft.AspNetCore.NodeServices.Npm;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using System.Net.Sockets;
using System.Net;
using System.Linq;

namespace Microsoft.AspNetCore.SpaServices.AngularCli
{
    internal class AngularCliMiddleware
    {
        private const string LogCategoryName = "Microsoft.AspNetCore.SpaServices";
        private const int TimeoutMilliseconds = 50 * 1000;

        internal readonly static string AngularCliMiddlewareKey = Guid.NewGuid().ToString();

        private readonly string _sourcePath;
        private readonly ILogger _logger;
        private readonly HttpClient _neverTimeOutHttpClient =
            ConditionalProxy.CreateHttpClientForProxy(Timeout.InfiniteTimeSpan);

        public AngularCliMiddleware(
            IApplicationBuilder appBuilder,
            string sourcePath,
            string npmScriptName,
            SpaDefaultPageMiddleware defaultPageMiddleware)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                throw new ArgumentException("Cannot be null or empty", nameof(sourcePath));
            }

            if (string.IsNullOrEmpty(npmScriptName))
            {
                throw new ArgumentException("Cannot be null or empty", nameof(npmScriptName));
            }

            _sourcePath = sourcePath;

            // If the DI system gives us a logger, use it. Otherwise, set up a default one.
            var loggerFactory = appBuilder.ApplicationServices.GetService<ILoggerFactory>();
            _logger = loggerFactory != null
                ? loggerFactory.CreateLogger(LogCategoryName)
                : new ConsoleLogger(LogCategoryName, null, false);

            // Start Angular CLI and attach to middleware pipeline
            var angularCliServerInfoTask = StartAngularCliServerAsync(npmScriptName);

            // Everything we proxy is hardcoded to target http://localhost because:
            // - the requests are always from the local machine (we're not accepting remote
            //   requests that go directly to the Angular CLI middleware server)
            // - given that, there's no reason to use https, and we couldn't even if we
            //   wanted to, because in general the Angular CLI server has no certificate
            var proxyOptionsTask = angularCliServerInfoTask.ContinueWith(
                task => new ConditionalProxyMiddlewareTarget(
                    "http", "localhost", task.Result.Port.ToString()));

            var applicationStoppingToken = GetStoppingToken(appBuilder);

            // Proxy all requests into the Angular CLI server
            appBuilder.Use(async (context, next) =>
            {
                try
                {
                    var didProxyRequest = await ConditionalProxy.PerformProxyRequest(
                        context, _neverTimeOutHttpClient, proxyOptionsTask, applicationStoppingToken);

                    // Since we are proxying everything, this is the end of the middleware pipeline.
                    // We won't call next().
                    if (!didProxyRequest)
                    {
                        context.Response.StatusCode = 404;
                    }
                }
                catch (AggregateException)
                {
                    ThrowIfTaskCancelled(angularCliServerInfoTask);
                    throw;
                }
                catch (TaskCanceledException)
                {
                    ThrowIfTaskCancelled(angularCliServerInfoTask);
                    throw;
                }
            });

            // Advertise the availability of this feature to other SPA middleware
            appBuilder.Properties.Add(AngularCliMiddlewareKey, this);
        }

        private void ThrowIfTaskCancelled(Task task)
        {
            if (task.IsCanceled)
            {
                throw new InvalidOperationException(
                    $"The Angular CLI process did not start listening for requests " +
                    $"within the timeout period of {TimeoutMilliseconds / 1000} seconds. " +
                    $"Check the log output for error information.");
            }
        }

        internal Task StartAngularCliBuilderAsync(string npmScriptName)
        {
            var npmScriptRunner = new NpmScriptRunner(
                _sourcePath, npmScriptName, "--watch");
            AttachToLogger(_logger, npmScriptRunner);

            return npmScriptRunner.StdOut.WaitForMatch(
                new Regex("chunk"), TimeoutMilliseconds);
        }

        private static CancellationToken GetStoppingToken(IApplicationBuilder appBuilder)
        {
            var applicationLifetime = appBuilder
                .ApplicationServices
                .GetService(typeof(IApplicationLifetime));
            return ((IApplicationLifetime)applicationLifetime).ApplicationStopping;
        }

        private async Task<AngularCliServerInfo> StartAngularCliServerAsync(string npmScriptName)
        {
            var portNumber = FindAvailablePort();
            _logger.LogInformation($"Starting @angular/cli on port {portNumber}...");

            var npmScriptRunner = new NpmScriptRunner(
                _sourcePath, npmScriptName, $"--port {portNumber}");
            AttachToLogger(_logger, npmScriptRunner);

            var openBrowserLine = await npmScriptRunner.StdOut.WaitForMatch(
                new Regex("open your browser on (http\\S+)"),
                TimeoutMilliseconds);
            var uri = new Uri(openBrowserLine.Groups[1].Value);
            var serverInfo = new AngularCliServerInfo { Port = uri.Port };

            // Even after the Angular CLI claims to be listening for requests, there's a short
            // period where it will give an error if you make a request too quickly. Give it
            // a moment to finish starting up.
            await Task.Delay(500);

            return serverInfo;
        }

        private static void AttachToLogger(ILogger logger, NpmScriptRunner npmScriptRunner)
        {
            // When the NPM task emits complete lines, pass them through to the real logger
            // But when it emits incomplete lines, assume this is progress information and
            // hence just pass it through to StdOut regardless of logger config.
            npmScriptRunner.CopyOutputToLogger(logger);

            npmScriptRunner.StdErr.OnReceivedChunk += chunk =>
            {
                var containsNewline = Array.IndexOf(
                    chunk.Array, '\n', chunk.Offset, chunk.Count) >= 0;
                if (!containsNewline)
                {
                    Console.Write(chunk.Array, chunk.Offset, chunk.Count);
                }
            };
        }

        private static int FindAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

#pragma warning disable CS0649
        class AngularCliServerInfo
        {
            public int Port { get; set; }
        }
    }
#pragma warning restore CS0649
}
