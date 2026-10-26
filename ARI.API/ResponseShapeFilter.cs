using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ARI.API;

/// <summary>
/// Gives every answer from the API one shape, so a client never has to guess:
///   a failure is always <c>{ "error": "what went wrong" }</c> with a status of 400 or more;
///   a success that has nothing to hand back is <c>{ "ok": true }</c>; any other success is the thing itself.
/// A controller can keep writing <c>BadRequest("text")</c>, <c>NotFound()</c> or <c>Ok()</c>; this turns them into the
/// shape above on the way out. Files, redirects and streams are left alone.
/// </summary>
public sealed class ResponseShapeFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        switch (context.Result)
        {
            case StatusCodeResult plain:
                context.Result = plain.StatusCode >= 400
                    ? Failure(plain.StatusCode, DefaultMessage(plain.StatusCode))
                    : new ObjectResult(new { ok = true }) { StatusCode = plain.StatusCode == 204 ? 200 : plain.StatusCode };
                break;

            // A failure that already says what went wrong in an "error" field is kept as written (it may carry extras,
            // such as how many sign-in attempts are left).
            case ObjectResult { StatusCode: >= 400 } failed when failed.Value is not ProblemDetails && HasErrorField(failed.Value):
                break;

            case ObjectResult { StatusCode: >= 400 } failed:
                context.Result = Failure(failed.StatusCode!.Value, MessageOf(failed.Value, failed.StatusCode.Value));
                break;

            // A framework-made validation failure (bad JSON, wrong type) has no explicit status; it carries ProblemDetails.
            case ObjectResult { Value: ProblemDetails problem }:
                context.Result = Failure(problem.Status ?? 400, MessageOf(problem, problem.Status ?? 400));
                break;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }

    private static ObjectResult Failure(int status, string message) => new(new { error = message }) { StatusCode = status };

    private static string MessageOf(object? value, int status) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => text,
        ValidationProblemDetails v when v.Errors.Count > 0 => v.Errors.First().Value.FirstOrDefault() is { Length: > 0 } first ? first : "The request was not valid.",
        ProblemDetails p when !string.IsNullOrWhiteSpace(p.Detail) => p.Detail,
        _ => DefaultMessage(status),
    };

    private static bool HasErrorField(object? value) => value?.GetType().GetProperty("error") is not null;

    private static string DefaultMessage(int status) => status switch
    {
        400 => "The request was not valid.",
        401 => "Sign in first.",
        403 => "You are not allowed to do this.",
        404 => "Not found.",
        409 => "That conflicts with what is already there.",
        415 => "That kind of file is not supported.",
        503 => "ARI is not ready for this yet.",
        _   => "Something went wrong.",
    };
}
