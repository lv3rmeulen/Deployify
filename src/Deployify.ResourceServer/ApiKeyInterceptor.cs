using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Deployify.ResourceServer;

/// <summary>
/// Requires the API key the dashboard sends in <c>x-resource-service-api-key</c> when its
/// <c>Dashboard:ResourceServiceClient:AuthMode</c> is <c>ApiKey</c>.
/// </summary>
internal sealed class ApiKeyInterceptor(string apiKey) : Interceptor
{
    /// <summary>The header Aspire's dashboard client sends the key in.</summary>
    public const string HeaderName = "x-resource-service-api-key";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, responseStream, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        var sent = context.RequestHeaders.GetValue(HeaderName);
        if (sent is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), _expected))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing or wrong resource service API key."));
        }
    }
}
