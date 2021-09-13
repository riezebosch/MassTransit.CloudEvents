﻿using System.Threading.Tasks;
using Bogus;
using FluentAssertions.Extensions;
using Hypothesist;
using DaprApp;
using DaprApp.Controllers;
using MassTransit.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wrapr;
using Xunit;
using Xunit.Abstractions;

namespace MassTransit.CloudEvents.IntegrationTests
{
    [Collection("user/loggedIn")]
    public class ToDapr
    {
        private readonly ITestOutputHelper _output;

        public ToDapr(ITestOutputHelper output) => 
            _output = output;

        [Fact]
        public async Task Do()
        {
            // Arrange
            var message = new Faker<UserLoggedIn>().CustomInstantiator(f => new UserLoggedIn(f.Random.Number())).Generate();
            var hypothesis = Hypothesis
                .For<int>()
                .Any(x => x == message.UserId);

            using var logger = _output.BuildLogger();
            using var host = await Host(hypothesis.ToHandler());
            await using var sidecar = await Sidecar(logger);
            
            // Act
            await Publish(message, logger);

            // Assert
            await hypothesis.Validate(10.Seconds());
        }
        
        
        private static async Task<Sidecar> Sidecar(ILogger logger)
        {
            var sidecar = new Sidecar("to-dapr", logger);
            await sidecar.Start(with => with
                .ComponentsPath("components")
                .AppPort(6000));

            return sidecar;
        }

        private async Task<Microsoft.Extensions.Hosting.IHost> Host(IHandler<int> handler)
        {
            var host = new HostBuilder().ConfigureWebHost(app => app
                    .UseStartup<Startup>()
                    .ConfigureLogging(builder => builder.AddXunit(_output))
                    .ConfigureServices(services => services.AddSingleton(handler))
                    .UseKestrel(options => options.ListenLocalhost(6000)))
                .Build();
            await host.StartAsync();
            return host;
        }

        private static async Task Publish(UserLoggedIn message, ILogger logger)
        {
            LogContext.ConfigureCurrentLogContext(logger);
            
            var bus = Bus.Factory
                .CreateUsingRabbitMq(cfg =>
                {
                    cfg.UseCloudEvents()
                        .Type<UserLoggedIn>("loggedIn");
                    
                    // set the topic/exchange
                    cfg.Message<UserLoggedIn>(x => 
                        x.SetEntityName("user/loggedIn"));

                    // if you don't want MassTransit to create the exchange
                    cfg.PublishTopology.GetMessageTopology<UserLoggedIn>().Exclude = true;
                });

            await bus.StartAsync();
            
            var endpoint = await bus.GetPublishSendEndpoint<UserLoggedIn>();
            await endpoint.Send(message);
        }

        public record UserLoggedIn(int UserId);
    }
}