﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Routing;
using AutoMapper;
using Exceptionless.Api.Extensions;
using Exceptionless.Api.Security;
using Exceptionless.Api.Serialization;
using Exceptionless.Api.Utility;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Jobs;
using Exceptionless.Core.Migrations;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Serialization;
using Exceptionless.Core.Utility;
using Exceptionless.Models;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using MongoDB.Driver;
using Newtonsoft.Json;
using Owin;
using SimpleInjector;
using SimpleInjector.Integration.WebApi;

namespace Exceptionless.Api {
    public static class AppBuilder {
        public static void Build(IAppBuilder app) {
            BuildWithContainer(app, CreateContainer());
        }

        public static void BuildWithContainer(IAppBuilder app, Container container, bool registerExceptionlessClient = true) {
            if (container == null)
                throw new ArgumentNullException("container");

            // if enabled, auto upgrade the database
            if (Settings.Current.ShouldAutoUpgradeDatabase) {
                var url = new MongoUrl(Settings.Current.MongoConnectionString);
                string databaseName = url.DatabaseName;
                if (Settings.Current.AppendMachineNameToDatabase)
                    databaseName += String.Concat("-", Environment.MachineName.ToLower());

                MongoMigrationChecker.EnsureLatest(Settings.Current.MongoConnectionString, databaseName);
            }

            Config = new HttpConfiguration();
            Config.DependencyResolver = new SimpleInjectorWebApiDependencyResolver(container);
            Config.Formatters.Remove(Config.Formatters.XmlFormatter);
            Config.Formatters.JsonFormatter.SerializerSettings.Formatting = Formatting.Indented;
            Config.Formatters.JsonFormatter.SerializerSettings.ContractResolver = new LowerCaseUnderscorePropertyNamesContractResolver();

            var constraintResolver = new DefaultInlineConstraintResolver();
            constraintResolver.ConstraintMap.Add("objectid", typeof(ObjectIdRouteConstraint));
            constraintResolver.ConstraintMap.Add("objectids", typeof(ObjectIdsRouteConstraint));
            Config.MapHttpAttributeRoutes(constraintResolver);
            //config.EnableSystemDiagnosticsTracing();

            container.RegisterSingle<JsonSerializer>(JsonSerializer.Create(new JsonSerializerSettings { ContractResolver = new SignalRContractResolver() }));
            container.RegisterWebApiFilterProvider(Config);

            try {
                container.Verify();
            } catch (Exception ex) {
                var tempEx = ex;
                while (!(tempEx is ReflectionTypeLoadException)) {
                    if (tempEx.InnerException == null)
                        break;
                    tempEx = tempEx.InnerException;
                }

                var typeLoadException = tempEx as ReflectionTypeLoadException;
                if (typeLoadException != null) {
                    foreach (var loaderEx in typeLoadException.LoaderExceptions)
                        Debug.WriteLine(loaderEx.Message);
                }

                Debug.WriteLine(ex.Message);
                throw;
            }

            Config.MessageHandlers.Add(container.GetInstance<XHttpMethodOverrideDelegatingHandler>());
            Config.MessageHandlers.Add(container.GetInstance<EncodingDelegatingHandler>());
            Config.MessageHandlers.Add(container.GetInstance<AuthMessageHandler>());

            // Throttle api calls to X every 15 minutes by IP address.
            Config.MessageHandlers.Add(container.GetInstance<ThrottlingHandler>());

            // Reject event posts in orgs over their max event limits.
            Config.MessageHandlers.Add(container.GetInstance<OverageHandler>());

            app.UseCors(CorsOptions.AllowAll);

            app.CreatePerContext<Lazy<User>>("User", ctx => new Lazy<User>(() => {
                if (ctx.Request.User == null || ctx.Request.User.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                string userId = ctx.Request.User.GetUserId();
                if (String.IsNullOrEmpty(userId))
                    return null;

                var userRepository = container.GetInstance<IUserRepository>();
                return userRepository.GetById(userId, true);
            }));

            app.CreatePerContext<Lazy<Project>>("DefaultProject", ctx => new Lazy<Project>(() => {
                if (ctx.Request.User == null || ctx.Request.User.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                string projectId = ctx.Request.User.GetDefaultProjectId();
                var projectRepository = container.GetInstance<IProjectRepository>();

                if (String.IsNullOrEmpty(projectId)) {
                    var firstOrgId = ctx.Request.User.GetOrganizationIds().FirstOrDefault();
                    if (!String.IsNullOrEmpty(firstOrgId)) {
                        var project = projectRepository.GetByOrganizationId(firstOrgId, useCache: true).FirstOrDefault();
                        if (project != null)
                            return project;
                    }

                    var dataHelper = container.GetInstance<DataHelper>();
                    // create a default org and project
                    projectId = dataHelper.CreateDefaultOrganizationAndProject(ctx.Request.GetUser());
                }

                return projectRepository.GetById(projectId, true);
            }));

            if (registerExceptionlessClient) {
                ExceptionlessClient.Default.RegisterWebApi(Config);
                Config.Services.Add(typeof(IExceptionLogger), new ExceptionlessExceptionLogger());
            }

            app.UseWebApi(Config);
            app.MapSignalR("/api/v2/push", new HubConfiguration { Resolver = new SimpleInjectorSignalRDependencyResolver(container) });

            PhysicalFileSystem fileSystem = null;
            var root = AppDomain.CurrentDomain.BaseDirectory;
            if (Directory.Exists(Path.Combine(root, "./Content")))
                fileSystem = new PhysicalFileSystem(Path.Combine(root, "./Content"));
            if (Directory.Exists(Path.Combine(root, "./bin/Content")))
                fileSystem = new PhysicalFileSystem(Path.Combine(root, "./bin/Content"));

            if (fileSystem != null)
                app.UseFileServer(new FileServerOptions { FileSystem = fileSystem });

            Mapper.Configuration.ConstructServicesUsing(container.GetInstance);

            var context = new OwinContext(app.Properties);
            var token = context.Get<CancellationToken>("host.OnAppDisposing");

            if (Settings.Current.EnableJobsModule) {
                Task.Factory.StartNew(() => container.GetInstance<ProcessEventPostsJob>().RunContinuousAsync(token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => container.GetInstance<ProcessEventUserDescriptionsJob>().RunContinuousAsync(token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => container.GetInstance<ProcessMailMessageJob>().RunContinuousAsync(token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => container.GetInstance<DailyNotificationJob>().RunContinuousAsync(delay: TimeSpan.FromMinutes(15), token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => container.GetInstance<EnforceRetentionLimitsJob>().RunContinuousAsync(delay: TimeSpan.FromHours(8), token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => container.GetInstance<RemoveStaleAccountsJob>().RunContinuousAsync(delay: TimeSpan.FromHours(8), token: token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        public static Container CreateContainer() {
            var container = new Container();
            container.Options.AllowOverridingRegistrations = true;
            container.Options.PropertySelectionBehavior = new InjectAttributePropertySelectionBehavior();

            container.RegisterPackage<Core.Bootstrapper>();
            container.RegisterPackage<Bootstrapper>();

            return container;
        }

        public static HttpConfiguration Config { get; private set; }
    }
}