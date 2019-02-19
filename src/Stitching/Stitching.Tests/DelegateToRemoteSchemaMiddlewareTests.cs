using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Net.Cache;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Snapshooter.Xunit;
using Xunit;
using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using HotChocolate.Stitching.Schemas.Contracts;
using HotChocolate.Stitching.Schemas.Customers;
using HotChocolate.Types;
using HotChocolate.Resolvers;
using HotChocolate.Stitching.Delegation;
using FileResource = ChilliCream.Testing.FileResource;
using HotChocolate.Language;
using HotChocolate.Stitching.Utilities;

namespace HotChocolate.Stitching
{
    public class DelegateToRemoteSchemaMiddlewareTests
        : IClassFixture<TestServerFactory>
    {
        public DelegateToRemoteSchemaMiddlewareTests(
            TestServerFactory testServerFactory)
        {
            TestServerFactory = testServerFactory;
        }

        private TestServerFactory TestServerFactory { get; set; }

        [Fact]
        public async Task ExecuteStitchingQueryWithInlineFragment()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryWithInlineFragment.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryWithFragmentDefinition()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryWithFragmentDefs.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryWithVariables()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryWithVariables.graphql"))
            {
                VariableValues = new Dictionary<string, object>
                {
                    {"customerId", "Q3VzdG9tZXIteDE="}
                }
            };

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryWithUnion()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryWithUnion.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryWithArguments()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryWithArguments.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact(Skip = "Not yet supported!")]
        public async Task ExecuteStitchingQueryDeepArrayPath()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryDeepArrayPath.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryDeepObjectPath()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryDeepObjectPath.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        [Fact]
        public async Task ExecuteStitchingQueryDeepScalarPath()
        {
            // arrange
            var request = new QueryRequest(FileResource.Open(
                "StitchingQueryDeepScalarPath.graphql"));

            // act
            IExecutionResult result = await ExecuteStitchedQuery(request);

            // assert
            result.MatchSnapshot();
        }

        private Task<IExecutionResult> ExecuteStitchedQuery(
            QueryRequest request)
        {
            // arrange
            IHttpClientFactory clientFactory = CreateRemoteSchemas();

            var serviceCollection = new ServiceCollection();

            serviceCollection.AddSingleton(clientFactory);

            serviceCollection.AddRemoteQueryExecutor(b => b
                .SetSchemaName("contract")
                .SetSchema(FileResource.Open("Contract.graphql"))
                .AddScalarType<DateTimeType>());

            serviceCollection.AddRemoteQueryExecutor(b => b
                .SetSchemaName("customer")
                .SetSchema(FileResource.Open("Customer.graphql")));

            serviceCollection.AddStitchedSchema(
                FileResource.Open("Stitching.graphql"),
                c => c.RegisterType<DateTimeType>());

            IServiceProvider services =
                request.Services =
                serviceCollection.BuildServiceProvider();

            IQueryExecutor executor = services
                .GetRequiredService<IQueryExecutor>();

            // act
            return executor.ExecuteAsync(request);
        }

        [Fact]
        public async Task ExecuteStitchedQueryWithComputedField()
        {
            // arrange
            IHttpClientFactory clientFactory = CreateRemoteSchemas();

            var serviceCollection = new ServiceCollection();

            serviceCollection.AddSingleton(clientFactory);

            serviceCollection.AddRemoteQueryExecutor(b => b
                .SetSchemaName("contract")
                .SetSchema(FileResource.Open("Contract.graphql"))
                .AddScalarType<DateTimeType>());

            serviceCollection.AddRemoteQueryExecutor(b => b
                .SetSchemaName("customer")
                .SetSchema(FileResource.Open("Customer.graphql")));

            serviceCollection.AddStitchedSchema(
                FileResource.Open("StitchingComputed.graphql"),
                c =>
                {
                    c.Map(new FieldReference("Customer", "foo"),
                        next => context =>
                        {
                            OrderedDictionary obj =
                                context.Parent<OrderedDictionary>();
                            context.Result = obj["name"] + "_" + obj["id"];
                            return Task.CompletedTask;
                        });
                    c.RegisterType<DateTimeType>();
                });

            var request = new QueryRequest(
                FileResource.Open("StitchingQueryComputedField.graphql"));

            IServiceProvider services =
                request.Services =
                serviceCollection.BuildServiceProvider();

            IQueryExecutor executor = services
                .GetRequiredService<IQueryExecutor>();

            // act
            IExecutionResult result = await executor.ExecuteAsync(request);

            // assert
            Snapshot.Match(result);
        }

        [Fact]
        public async Task ExecuteStitchedQueryBuilder()
        {
            // arrange
            IHttpClientFactory clientFactory = CreateRemoteSchemas();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(clientFactory);
            serviceCollection.AddStitchedSchema(builder =>
                builder.AddSchemaFromHttp("contract")
                    .AddSchemaFromHttp("customer")
                    .RenameField("customer",
                        new FieldReference("Customer", "name"), "foo")
                    .AddExtensionsFromString(
                        FileResource.Open("StitchingExtensions.graphql")));

            IServiceProvider services =
                serviceCollection.BuildServiceProvider();

            IQueryExecutor executor = services
                .GetRequiredService<IQueryExecutor>();
            IExecutionResult result = null;

            // act
            using (IServiceScope scope = services.CreateScope())
            {
                var request = new QueryRequest(@"
                {
                    a: customer(id: ""Q3VzdG9tZXIteDE="") {
                        bar: foo
                        contracts {
                            id
                        }
                    }

                    b: customer(id: ""Q3VzdG9tZXIteDE="") {
                        foo
                        contracts {
                            id
                        }
                    }
                }");
                request.Services = scope.ServiceProvider;

                result = await executor.ExecuteAsync(request);
            }

            // assert
            Snapshot.Match(result);
        }

        [Fact]
        public async Task ExecuteStitchedQueryBuilderVariableArguments()
        {
            // arrange
            IHttpClientFactory clientFactory = CreateRemoteSchemas();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(clientFactory);
            serviceCollection.AddStitchedSchema(builder =>
                builder.AddSchemaFromHttp("contract")
                    .AddSchemaFromHttp("customer")
                    .RenameField("customer",
                        new FieldReference("Customer", "name"), "foo")
                    .AddExtensionsFromString(
                        FileResource.Open("StitchingExtensions.graphql")));

            IServiceProvider services =
                serviceCollection.BuildServiceProvider();

            IQueryExecutor executor = services
                .GetRequiredService<IQueryExecutor>();
            IExecutionResult result = null;

            // act
            using (IServiceScope scope = services.CreateScope())
            {
                var request = new QueryRequest(@"
                query a($id: ID!) {
                    a: customer2(customerId: $id) {
                        bar: foo
                        contracts {
                            id
                        }
                    }
                }");
                request.VariableValues = new Dictionary<string, object>
                {
                    {"id", "Q3VzdG9tZXIteDE="}
                };
                request.Services = scope.ServiceProvider;

                result = await executor.ExecuteAsync(request);
            }

            // assert
            Snapshot.Match(result);
        }

        private IHttpClientFactory CreateRemoteSchemas()
        {
            TestServer server_contracts = TestServerFactory.Create(
                ContractSchemaFactory.ConfigureSchema,
                ContractSchemaFactory.ConfigureServices,
                new QueryMiddlewareOptions());

            TestServer server_customers = TestServerFactory.Create(
                CustomerSchemaFactory.ConfigureSchema,
                CustomerSchemaFactory.ConfigureServices,
                new QueryMiddlewareOptions());

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(t => t.CreateClient(It.IsAny<string>()))
                .Returns(new Func<string, HttpClient>(n =>
                {
                    return n.Equals("contract")
                        ? server_contracts.CreateClient()
                        : server_customers.CreateClient();
                }));
            return httpClientFactory.Object;
        }
    }
}
