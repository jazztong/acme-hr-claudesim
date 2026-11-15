using System.Text.Json;
using Anthropic.Models.Messages;
using Api.Services;

namespace Api.Documents;

/// <summary>Lab 6: turn a document into checked data, or send it to a person.</summary>
public class DocumentExtractor(IClaudeChat claude, ClaudeOptions options)
{
    public const int MaxAttempts = 2; // first try + one retry

    const string Prompt = """
        Extract the data from this Acme HR document. It is a medical certificate (MC), an expense claim, or something else (kind "other").
        Dates as yyyy-MM-dd. Amounts as numbers without currency symbols. Use null for the part that does not apply.
        Copy values exactly as written. Do not fix or invent numbers.
        clinic: the clinic's name from the letterhead, including any words in brackets that belong to the letterhead (for example "(synthetic)").
        """;

    /// <summary>The JSON schema Claude must follow (structured outputs).</summary>
    public static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "kind", "medical_certificate", "expense_claim" },
        properties = new
        {
            kind = new { type = "string", @enum = new[] { "medical_certificate", "expense_claim", "other" } },
            medical_certificate = new
            {
                type = new[] { "object", "null" },
                additionalProperties = false,
                required = new[] { "employee_name", "clinic", "date_from", "date_to", "days" },
                properties = new
                {
                    employee_name = new { type = "string" }, clinic = new { type = "string" },
                    date_from = new { type = "string" }, date_to = new { type = "string" }, days = new { type = "integer" },
                },
            },
            expense_claim = new
            {
                type = new[] { "object", "null" },
                additionalProperties = false,
                required = new[] { "claim_no", "vendor", "date", "currency", "subtotal", "tax", "total", "items" },
                properties = new
                {
                    claim_no = new { type = "string" }, vendor = new { type = "string" }, date = new { type = "string" },
                    currency = new { type = "string" }, subtotal = new { type = "number" }, tax = new { type = "number" }, total = new { type = "number" },
                    items = new
                    {
                        type = "array",
                        items = new { type = "object", additionalProperties = false, required = new[] { "description", "amount" }, properties = new { description = new { type = "string" }, amount = new { type = "number" } } },
                    },
                },
            },
        },
    }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    /// <summary>The first message: the PDF as a document block (Claude reads its text and its pages as images), then the ask.</summary>
    public static UserMessageParam DocumentMessage(byte[] pdf) => new()
    {
        Content = new List<ContentBlockParam>
        {
            new DocumentBlockParam { Source = new Base64PdfSource { Data = Convert.ToBase64String(pdf) } },
            new TextBlockParam { Text = "Extract the data from this document." },
        },
    };

    /// <summary>Claude reads, C# decides: the schema fixes the shape, DocumentValidator checks the meaning.
    /// At most MaxAttempts calls; anything still failing goes to a person (NeedsReview).</summary>
    public async Task<ExtractionResult> ExtractAsync(byte[] pdf, CancellationToken ct = default)
    {
        var history = new List<MessageParam> { DocumentMessage(pdf) };
        Extraction? data = null;
        List<string> problems = [];

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var res = await claude.SendAsync(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 1024,
                System = Prompt,
                Messages = history.ToList(),
                OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = Schema } },
            }, ct);

            // A cut-off or refused answer is not trusted, even if it happens to parse.
            if (res.StopReason != StopReason.EndTurn)
                return new ExtractionResult(null, DocumentStatus.NeedsReview,
                    [$"Claude stopped with {res.StopReason?.Raw() ?? "unknown"}, so the answer may be incomplete"], attempt);

            (data, problems) = Parse(TextOf(res));
            if (problems.Count == 0) return new ExtractionResult(data, DocumentStatus.Valid, [], attempt);
            if (data?.Kind == "other") return new ExtractionResult(data, DocumentStatus.NeedsReview, problems, attempt);

            if (attempt < MaxAttempts)
            {
                history.Add(new AssistantMessageParam { Content = res.Content.Select(b => new ContentBlockParam(b.Json)).ToList() });
                history.Add(new UserMessageParam { Content = RetryMessage(problems) });
            }
        }
        return new ExtractionResult(data, DocumentStatus.NeedsReview, problems, MaxAttempts);
    }

    static string TextOf(Message res) => string.Concat(res.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));

    /// <summary>Parse in C# and run the business rules. Text that is not JSON is a problem, not a crash.</summary>
    static (Extraction? Data, List<string> Problems) Parse(string text)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Extraction>(text);
            return data is null ? (null, ["the answer is not valid JSON: it is empty"]) : (data, DocumentValidator.Check(data));
        }
        catch (JsonException e)
        {
            return (null, [$"the answer is not valid JSON: {e.Message}"]);
        }
    }

    /// <summary>The exact problems, and a rule against "fixing" a document that is wrong on paper (samples 08, 10, 17).</summary>
    static string RetryMessage(IEnumerable<string> problems) => $"""
        Your answer has these problems:
        {string.Join("\n", problems.Select(p => $"- {p}"))}
        Look at the document again. Correct only reading mistakes; if the document itself is wrong, keep its values as written.
        Return the full JSON again.
        """;
}
