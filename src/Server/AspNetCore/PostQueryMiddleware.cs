using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HotChocolate.Execution;
using Newtonsoft.Json;
using HotChocolate.Language;

#if ASPNETCLASSIC
using Microsoft.Owin;
using HttpContext = Microsoft.Owin.IOwinContext;
using RequestDelegate = Microsoft.Owin.OwinMiddleware;
#else
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
#endif

#if ASPNETCLASSIC
namespace HotChocolate.AspNetClassic
#else
namespace HotChocolate.AspNetCore
#endif
{
    public class PostQueryMiddleware
        : QueryMiddlewareBase
    {
        public PostQueryMiddleware(
            RequestDelegate next,
            IQueryExecutor queryExecutor,
            IQueryResultSerializer resultSerializer,
            QueryMiddlewareOptions options)
                : base(next, queryExecutor, resultSerializer, options)
        { }

        protected override bool CanHandleRequest(HttpContext context)
        {
            return string.Equals(
                context.Request.Method,
                HttpMethods.Post,
                StringComparison.Ordinal);
        }

        protected override async Task<IQueryRequestBuilder>
            CreateQueryRequestAsync(HttpContext context)
        {
            QueryRequestDto request = await ReadRequestAsync(context)
                .ConfigureAwait(false);

            return QueryRequestBuilder.New()
                .SetQuery(request.Query)
                .SetOperation(request.OperationName)
                .SetVariableValues(
                    QueryMiddlewareUtilities.ToDictionary(request.Variables));
        }

        private static async Task<GraphQLRequest> ReadRequestAsync(
            HttpContext context)
        {

            using (Stream stream = context.Request.Body)
            {




            }



            using (var reader = new StreamReader(context.Request.Body,
                Encoding.UTF8))
            {
                string content = await reader.ReadToEndAsync()
                    .ConfigureAwait(false);

                switch (context.Request.ContentType.Split(';')[0])
                {
                    case ContentType.Json:

                        return JsonConvert.DeserializeObject<QueryRequestDto>(
                            content, QueryMiddlewareUtilities.JsonSettings);

                    case ContentType.GraphQL:
                        return new QueryRequestDto { Query = content };

                    default:
                        throw new NotSupportedException();
                }
            }
        }


#if !ASPNETCLASSIC

        public async Task FillPipeAsync(Stream request, PipeWriter writer)
        {
            const int minimumBufferSize = 512;

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                try
                {
                    int bytesRead = await request. (memory, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    // Tell the PipeWriter how much was read from the Socket
                    writer.Advance(bytesRead);
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    break;
                }

                // Make the data available to the PipeReader
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Tell the PipeReader that there's no more data coming
            writer.Complete();
        }

#endif
    }
}
