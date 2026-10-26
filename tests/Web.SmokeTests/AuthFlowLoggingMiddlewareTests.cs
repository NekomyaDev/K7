using K7.Server.Web.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace K7.Server.Web.SmokeTests;

[TestFixture]
public class AuthFlowLoggingMiddlewareTests
{
    [Test]
    public void IsAuthPath_ShouldCoverNativeLoginSurface()
    {
        AuthFlowLoggingMiddleware.IsAuthPath("/sign-in").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/sign-in/two-factor").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/welcome").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/connect/authorize").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/connect/token").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/auth/complete").Should().BeTrue();
        AuthFlowLoggingMiddleware.IsAuthPath("/api/users/me").Should().BeFalse();
        AuthFlowLoggingMiddleware.IsAuthPath("/library").Should().BeFalse();
    }

    [Test]
    public void ReadReturnUrl_ShouldPreferFormOverQuery()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?ReturnUrl=/welcome");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["ReturnUrl"] = "/connect/authorize?client_id=k7-native"
        });

        AuthFlowLoggingMiddleware.ReadReturnUrl(context.Request)
            .Should().StartWith("/connect/authorize");
    }

    [Test]
    public void CanReadForm_ShouldBeFalse_WhenAntiforgeryInvalid()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Features.Set<IAntiforgeryValidationFeature>(new StubAntiforgeryFeature(isValid: false));

        AuthFlowLoggingMiddleware.CanReadForm(context.Request).Should().BeFalse();
    }

    [Test]
    public void ReadReturnUrl_ShouldFallBackToQuery_WhenAntiforgeryInvalid()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?ReturnUrl=/connect/authorize?client_id=k7-native");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Features.Set<IAntiforgeryValidationFeature>(new StubAntiforgeryFeature(isValid: false));
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["ReturnUrl"] = "/should-not-use"
        });

        AuthFlowLoggingMiddleware.ReadReturnUrl(context.Request)
            .Should().StartWith("/connect/authorize");
    }

    [Test]
    public async Task InvokeAsync_ShouldNotThrow_WhenFormReadWouldFailAfterNext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/sign-in";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Features.Set<IAntiforgeryValidationFeature>(new StubAntiforgeryFeature(isValid: false));
        context.Features.Set<IFormFeature>(new ThrowingFormFeature());

        RequestDelegate next = _ =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        };

        var middleware = new AuthFlowLoggingMiddleware(next, NullLogger<AuthFlowLoggingMiddleware>.Instance);

        var act = async () => await middleware.InvokeAsync(context);

        await act.Should().NotThrowAsync();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private sealed class StubAntiforgeryFeature(bool isValid) : IAntiforgeryValidationFeature
    {
        public bool IsValid { get; } = isValid;

        public Exception? Error { get; } = isValid
            ? null
            : new InvalidOperationException(
                "This form is being accessed with an invalid anti-forgery token. Validate the `IAntiforgeryValidationFeature` on the request before reading from the form.");
    }

    private sealed class ThrowingFormFeature : IFormFeature
    {
        public bool HasFormContentType => true;

        public IFormCollection? Form
        {
            get => throw CreateAntiforgeryException();
            set => throw new NotSupportedException();
        }

        public IFormCollection ReadForm() => throw CreateAntiforgeryException();

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken = default) =>
            throw CreateAntiforgeryException();

        private static InvalidOperationException CreateAntiforgeryException() =>
            new("This form is being accessed with an invalid anti-forgery token. Validate the `IAntiforgeryValidationFeature` on the request before reading from the form.");
    }
}
