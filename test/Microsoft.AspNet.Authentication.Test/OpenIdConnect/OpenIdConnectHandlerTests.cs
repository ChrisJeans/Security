﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNet.Authentication.OpenIdConnect;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.TestHost;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Logging;
using Microsoft.Framework.OptionsModel;
using Microsoft.Framework.WebEncoders;
using Microsoft.IdentityModel.Protocols;
using Shouldly;
using Xunit;

namespace Microsoft.AspNet.Authentication.Tests.OpenIdConnect
{
    /// <summary>
    /// These tests are designed to test OpenIdConnectAuthenticationHandler.
    /// </summary>
    public class OpenIdConnectHandlerTests
    {
        /// <summary>
        /// Sanity check that logging is filtering, hi / low water marks are checked
        /// </summary>
        [Fact]
        public void LoggingLevel()
        {
            var logger = new CustomLogger(LogLevel.Debug);
            logger.IsEnabled(LogLevel.Critical).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Debug).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Error).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Information).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Verbose).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Warning).ShouldBe<bool>(true);

            logger = new CustomLogger(LogLevel.Critical);
            logger.IsEnabled(LogLevel.Critical).ShouldBe<bool>(true);
            logger.IsEnabled(LogLevel.Debug).ShouldBe<bool>(false);
            logger.IsEnabled(LogLevel.Error).ShouldBe<bool>(false);
            logger.IsEnabled(LogLevel.Information).ShouldBe<bool>(false);
            logger.IsEnabled(LogLevel.Verbose).ShouldBe<bool>(false);
            logger.IsEnabled(LogLevel.Warning).ShouldBe<bool>(false);
        }

        /// <summary>
        /// Test <see cref="OpenIdConnectAuthenticationHandler.AuthenticateCoreAsync"/> produces expected logs.
        /// Each call to 'RunVariation' is configured with an <see cref="OpenIdConnectAuthenticationOptions"/> and <see cref="OpenIdConnectMessage"/>.
        /// The list of expected log entries is checked and any errors reported.
        /// <see cref="CustomLoggerFactory"/> captures the logs so they can be analyzed.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task AuthenticateCoreLogging()
        {
            var errors = new Dictionary<string, List<Tuple<LogEntry, LogEntry>>>();
            var message = new OpenIdConnectMessage()
            {
                Code = Guid.NewGuid().ToString()         
            };

            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[]{ 0, 1, 7, 14, 15 }), CodeReceivedHandledOptions, message, errors);
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 7, 14, 16 }), CodeReceivedSkippedOptions, message, errors);
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 7, 14 }), Default.Options, message, errors);
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 2 }), MessageReceivedHandledOptions, message, errors);
            await RunVariation(LogLevel.Information, LoggingUtilities.PopulateLogEntries(new int[] { 2 }), MessageReceivedHandledOptions, message, errors);
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 3 }), MessageReceivedSkippedOptions, message, errors);
            await RunVariation(LogLevel.Information, LoggingUtilities.PopulateLogEntries(new int[] { 3 }), MessageReceivedSkippedOptions, message, errors);

            message.IdToken = "invalid";
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 7, 20, 8 }), SecurityTokenReceivedHandledOptions, message, errors);
            await RunVariation(LogLevel.Debug, LoggingUtilities.PopulateLogEntries(new int[] { 0, 1, 7, 20, 9 }), SecurityTokenReceivedSkippedOptions, message, errors);

            DisplayErrors(errors);
            errors.Count.ShouldBe(0);
        }

        /// <summary>
        /// Tests that <see cref="OpenIdConnectAuthenticationHandler"/> processed a message as expected.
        /// The test runs two independant paths: Using <see cref="ConfigureOptions{TOptions}"/> and <see cref="IOptions{TOptions}"/>.
        /// </summary>
        /// <param name="logLevel"><see cref="LogLevel"/> for this variation</param>
        /// <param name="expectedLogs">the expected log entries</param>
        /// <param name="action">the <see cref="OpenIdConnectAuthenticationOptions"/> delegate used for setting the options.</param>
        /// <param name="errors">container for propogation of errors.</param>
        /// <remarks>Note: create a new <see cref="OpenIdConnectAuthenticationHandler"/> for PostAsync. Internal state is maintained this will cause issue.</remarks>
        private async Task RunVariation(LogLevel logLevel, List<LogEntry> expectedLogs, Action<OpenIdConnectAuthenticationOptions> action, OpenIdConnectMessage message, Dictionary<string, List<Tuple<LogEntry, LogEntry>>> errors)
        {
            string variation = action.Method.ToString().Substring(5, action.Method.ToString().IndexOf('(') - 5);
            DisplayLogs(expectedLogs, "\n======\nVariation: " + variation + ", LogLevel: " + logLevel.ToString() + Environment.NewLine + Environment.NewLine + "Expected Logs: ");

            Debug.WriteLine(Environment.NewLine + "Logs using ConfigureOptions:");
            var encoder = UrlEncoder.Default;
            var dataFormater = new AuthenticationPropertiesFormaterKeyValue();
            var handler = new CustomOpenIdConnectAuthenticationHandler(EmptyTask, EmptyChallenge, ReturnTrue);
            var loggerFactory = new CustomLoggerFactory(logLevel);
            var server = CreateServer(new CustomConfigureOptions(action), encoder, loggerFactory, handler);

            message.State = OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey + "=" + encoder.UrlEncode(dataFormater.Protect(new AuthenticationProperties()));

            await server.CreateClient().PostAsync("http://localhost", new FormUrlEncodedContent(message.Parameters));
            LoggingUtilities.CheckLogs(variation + ":ConfigOptions, LogLevel: " + logLevel.ToString(), loggerFactory.Logger.Logs, expectedLogs, errors);

            Debug.WriteLine(Environment.NewLine + "Logs using IOptions:");
            handler = new CustomOpenIdConnectAuthenticationHandler(EmptyTask, EmptyChallenge, ReturnTrue);
            server = CreateServer(new Options(action), encoder, new CustomLoggerFactory(logLevel), handler);

            await server.CreateClient().PostAsync("http://localhost", new FormUrlEncodedContent(message.Parameters));
            LoggingUtilities.CheckLogs(variation + ":IOptions, LogLevel: " + logLevel.ToString(), loggerFactory.Logger.Logs, expectedLogs, errors);
        }

        /// <summary>
        /// Tests for receiving 'state' null or empty string
        /// </summary>
        [Theory]
        [InlineData(null, new int[] { 0, 1, 4 }, "AuthenticateEmptyOrNullState(null): ")]
        [InlineData("", new int[] { 0, 1, 4 }, "AuthenticateEmptyOrNullState(emptystring): ")]
        public async Task AuthenticateEmptyOrNullState(string state, int[] logsEntriesExpected, string variation)
        {
            var message =
                new OpenIdConnectMessage
                {
                    State = state,
                };

            var expectedLogs = LoggingUtilities.PopulateLogEntries(logsEntriesExpected);
            var loggerFactory = new CustomLoggerFactory(LogLevel.Debug);
            var handler = new CustomOpenIdConnectAuthenticationHandler(EmptyTask, EmptyChallenge, ReturnTrue);
            var server = CreateServer(new CustomConfigureOptions(Default.Options), UrlEncoder.Default, loggerFactory, handler);
            var form = new FormUrlEncodedContent(message.Parameters);

            await server.CreateClient().PostAsync(Default.LocalHost, form);

            var errors = new Dictionary<string, List<Tuple<LogEntry, LogEntry>>>();
            DisplayLogs(expectedLogs, variation + "Expected Logs");
            LoggingUtilities.CheckLogs(variation, loggerFactory.Logger.Logs, expectedLogs, errors);
            DisplayErrors(errors);
            errors.Count.ShouldBe<int>(0);
        }

        /// <summary>
        /// Tests for receiving 'state' with or without user data
        /// </summary>
        [Theory]
        [MemberData(nameof(WithOrWithoutUserDataState))]
        public async Task AuthenticateWithOrWithOutUserDataState(AuthenticationProperties properties, string userState, int[] logsEntriesExpected, string variation)
        {
            var expectedLogs = LoggingUtilities.PopulateLogEntries(logsEntriesExpected);
            var loggerFactory = new CustomLoggerFactory(LogLevel.Debug);
            var handler = new CustomOpenIdConnectAuthenticationHandler(EmptyTask, EmptyChallenge, ReturnTrue);
            var server = CreateServer(new CustomConfigureOptions(Default.Options), UrlEncoder.Default, loggerFactory, handler);
            var state = OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey + "=" + handler.OptionsPublic.StateDataFormat.Protect(properties);

            if (!string.IsNullOrWhiteSpace(userState))
                state += "&" + userState;

            var message =
                new OpenIdConnectMessage
                {
                    State = state,
                };

            var form = new FormUrlEncodedContent(message.Parameters);

            await server.CreateClient().PostAsync(Default.LocalHost, form);

            var errors = new Dictionary<string, List<Tuple<LogEntry, LogEntry>>>();
            DisplayLogs(expectedLogs, variation + "Expected Logs");
            LoggingUtilities.CheckLogs(variation, loggerFactory.Logger.Logs, expectedLogs, errors);
            DisplayErrors(errors);
            errors.Count.ShouldBe<int>(0);
        }

        /// <summary>
        /// Data for 'state' tests
        /// </summary>
        public static IEnumerable<object[]> WithOrWithoutUserDataState
        {
            get
            {
                return new List<object[]>
                { 
                    new object[] 
                    {
                        new AuthenticationProperties(),
                        null,
                        new int[] { 0, 1, 7 },
                        "AuthenticateWithOrWithOutUserDataState: new AuthenticationProperties()"
                    },
                    new object[] 
                    {
                        new AuthenticationProperties(),
                        "UserState",
                        new int[] { 0, 1, 7 },
                        "AuthenticateWithOrWithOutUserDataState: new AuthenticationProperties(), 'UserState'"
                    },
                    new object[]
                    {
                        new AuthenticationProperties()
                        {
                            RedirectUri = Default.LocalHost
                        },
                        "UserState",
                        new int[] { 0, 1, 7 },
                        "AuthenticateWithOrWithOutUserDataState: new AuthenticationProperties(), 'UserState'"
                    }
                };
            }
        }

        private void DisplayLogs(List<LogEntry> logs, string message = null)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Debug.WriteLine(message);

            foreach (var logentry in logs)
                Debug.WriteLine(logentry.ToString());
        }

        private void DisplayErrors(Dictionary<string, List<Tuple<LogEntry, LogEntry>>> errors)
        {
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Debug.WriteLine("Error in Variation: " + error.Key);
                    foreach (var logError in error.Value)
                    {
                        Debug.WriteLine("*Captured*, *Expected* : *" + (logError.Item1?.ToString() ?? "null") + "*, *" + (logError.Item2?.ToString() ?? "null") + "*");
                    }
                    Debug.WriteLine(Environment.NewLine);
                }
            }
        }

        #region HandlerTasks

        private static void EmptyChallenge(ChallengeContext context) { }

        private static Task EmptyTask() { return Task.FromResult(0); }

        private static bool ReturnTrue(string authenticationScheme)
        {
            return true;
        }

        #endregion

        #region Configure Options

        private static void CodeReceivedHandledOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    AuthorizationCodeReceived = (notification) =>
                    {
                        notification.HandleResponse();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void CodeReceivedSkippedOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    AuthorizationCodeReceived = (notification) =>
                    {
                        notification.SkipToNextMiddleware();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void MessageReceivedHandledOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    MessageReceived = (notification) =>
                    {
                        notification.HandleResponse();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void MessageReceivedSkippedOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    MessageReceived = (notification) =>
                    {
                        notification.SkipToNextMiddleware();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void SecurityTokenReceivedHandledOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    SecurityTokenReceived = (notification) =>
                    {
                        notification.HandleResponse();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void SecurityTokenReceivedSkippedOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    SecurityTokenReceived = (notification) =>
                    {
                        notification.SkipToNextMiddleware();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void SecurityTokenValidatedHandledOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    SecurityTokenValidated = (notification) =>
                    {
                        notification.HandleResponse();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        private static void SecurityTokenValidatedSkippedOptions(OpenIdConnectAuthenticationOptions options)
        {
            Default.Options(options);
            options.Notifications =
                new OpenIdConnectAuthenticationNotifications
                {
                    SecurityTokenValidated = (notification) =>
                    {
                        notification.SkipToNextMiddleware();
                        return Task.FromResult<object>(null);
                    }
                };
        }

        #endregion

        private static TestServer CreateServer(IOptions<OpenIdConnectAuthenticationOptions> options, IUrlEncoder encoder, ILoggerFactory loggerFactory, OpenIdConnectAuthenticationHandler handler = null)
        {
            return TestServer.Create(
                app =>
                {
                    app.UseCustomOpenIdConnectAuthentication(options, encoder, loggerFactory, handler);
                    app.Use(async (context, next) =>
                    {
                        await next();
                    });
                },
                services =>
                {
                    services.AddWebEncoders();
                    services.AddDataProtection();
                }
            );
        }

        private static TestServer CreateServer(CustomConfigureOptions configureOptions, IUrlEncoder encoder, ILoggerFactory loggerFactory, OpenIdConnectAuthenticationHandler handler = null)
        {
            return TestServer.Create(
                app =>
                {
                    app.UseCustomOpenIdConnectAuthentication(configureOptions, encoder, loggerFactory, handler);
                    app.Use(async (context, next) =>
                    {
                        await next();
                    });
                },
                services =>
                {
                    services.AddWebEncoders();
                    services.AddDataProtection();
                }
            );
        }
    }
}
