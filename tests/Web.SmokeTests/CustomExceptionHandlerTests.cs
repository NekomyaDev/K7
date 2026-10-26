using K7.Server.Web.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace K7.Server.Web.SmokeTests;

[TestFixture]
public class CustomExceptionHandlerTests
{
    [Test]
    public void IsAntiforgeryFormAccessException_ShouldDetectAspNetMessage()
    {
        var ex = new InvalidOperationException(
            "This form is being accessed with an invalid anti-forgery token. Validate the `IAntiforgeryValidationFeature` on the request before reading from the form.");

        CustomExceptionHandler.IsAntiforgeryFormAccessException(ex).Should().BeTrue();
        CustomExceptionHandler.IsAntiforgeryFormAccessException(new InvalidOperationException("other")).Should().BeFalse();
        CustomExceptionHandler.IsAntiforgeryFormAccessException(new Exception("wrap", ex)).Should().BeTrue();
    }

    [Test]
    public async Task TryHandleAsync_ShouldRedirectSignIn_WhenAntiforgeryFormAccessFails()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/sign-in";
        context.Request.QueryString = new QueryString("?ReturnUrl=/connect/authorize");
        context.Response.Body = new MemoryStream();

        var handler = new CustomExceptionHandler();
        var ex = new InvalidOperationException(
            "This form is being accessed with an invalid anti-forgery token. Validate the `IAntiforgeryValidationFeature` on the request before reading from the form.");

        var handled = await handler.TryHandleAsync(context, ex, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("/sign-in?ReturnUrl=/connect/authorize");
    }

    [Test]
    public async Task TryHandleAsync_ShouldReturnBadRequestJson_WhenAntiforgeryFailsOnApi()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/users/me";
        context.Response.Body = new MemoryStream();

        var handler = new CustomExceptionHandler();
        var ex = new InvalidOperationException(
            "This form is being accessed with an invalid anti-forgery token.");

        var handled = await handler.TryHandleAsync(context, ex, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
