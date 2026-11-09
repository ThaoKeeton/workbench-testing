using System.Text;
using System.Text.Json;
using Workbench.Sdk;
namespace Workbench.Testing.Domain.Sources;

// Two deliberately local schema1 formats. Neither document IDs nor locators are interpreted as paths/URLs.
internal static class LocalSnapshotParser
{
    internal const int ByteLimit = 1024 * 1024;
    internal static TestSourceDraft Parse(ReadOnlyMemory<byte> input, ITestSourceProvider provider, string collection, string itemTitle)
    {
        if (input.Length>ByteLimit) throw DomainErrors.Error("StoreLimitExceeded");
        if (input.IsEmpty) throw DomainErrors.Error("InvalidTestSource");
        var original = input.ToArray();
        ReadOnlyMemory<byte> json = original;
        if (json.Span.StartsWith(new byte[] {239,187,191})) json = json[3..];
        try
        {
            _ = new UTF8Encoding(false,true).GetCharCount(json.Span);
            ValidateTokens(json.Span);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth=16 });
            var root = Fields(document.RootElement, ["schemaVersion","documentId","title",collection]);
            if (root["schemaVersion"].ValueKind != JsonValueKind.Number || !root["schemaVersion"].TryGetInt32(out var schema) || schema!=1) throw DomainErrors.Error("InvalidTestSource");
            var documentId = Text(root["documentId"],200,true);
            var title = Text(root["title"],200,true);
            var items = Array(root[collection],256);
            var locators = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<TestSourceItem>();
            foreach (var item in items)
            {
                var fields = Fields(item,["id",itemTitle,"description","acceptanceCriteria","virtualScenario"]);
                var id = Text(fields["id"],512,true);
                if (!locators.Add(id)) throw DomainErrors.Error("InvalidTestSource");
                var criteria = Array(fields["acceptanceCriteria"],16).Select(x=>Text(x,4096,true)).ToArray();
                var scenarioValue = fields["virtualScenario"];
                TestVirtualScenario? scenario = scenarioValue.ValueKind == JsonValueKind.Null ? null : Text(scenarioValue,40,true) switch
                {
                    "TaskFormPositive" => TestVirtualScenario.TaskFormPositive,
                    "TaskFormEmptyTitle" => TestVirtualScenario.TaskFormEmptyTitle,
                    "TaskFormAssertionFailure" => TestVirtualScenario.TaskFormAssertionFailure,
                    _ => throw DomainErrors.Error("InvalidTestSource")
                };
                result.Add(new(id,Text(fields[itemTitle],200,true),Text(fields["description"],4096,false),System.Array.AsReadOnly(criteria),scenario));
            }
            return new(provider.Kind,provider.Id,provider.Version,documentId,title,original,result.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException)
        { throw DomainErrors.Error("InvalidTestSource"); }
    }
    private static Dictionary<string,JsonElement> Fields(JsonElement element,string[] required)
    {
        if (element.ValueKind!=JsonValueKind.Object) throw DomainErrors.Error("InvalidTestSource");
        var fields = new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!required.Contains(field.Name,StringComparer.Ordinal) || !fields.TryAdd(field.Name,field.Value)) throw DomainErrors.Error("InvalidTestSource");
        if (fields.Count!=required.Length) throw DomainErrors.Error("InvalidTestSource");
        return fields;
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement element,int max)
    {
        if (element.ValueKind!=JsonValueKind.Array || element.GetArrayLength()==0) throw DomainErrors.Error("InvalidTestSource");
        if (element.GetArrayLength()>max) throw DomainErrors.Error("StoreLimitExceeded");
        return element.EnumerateArray();
    }
    private static string Text(JsonElement element,int max,bool required)
    {
        if (element.ValueKind!=JsonValueKind.String) throw DomainErrors.Error("InvalidTestSource");
        var value=element.GetString()!;
        if (!DomainErrors.ValidUnicode(value) || required && string.IsNullOrWhiteSpace(value)) throw DomainErrors.Error("InvalidTestSource");
        if (value.Length>max) throw DomainErrors.Error("StoreLimitExceeded");
        return value;
    }
    private static void ValidateTokens(ReadOnlySpan<byte> bytes)
    {
        var reader=new Utf8JsonReader(bytes,new JsonReaderOptions { MaxDepth=16 });
        while(reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName) || !reader.ValueIsEscaped) continue;
            // Reject unpaired escaped UTF16 before GetString could substitute replacement characters.
            var token=reader.ValueSpan;
            for(var i=0;i<token.Length;i++)
            {
                if(token[i]!='\\') continue;
                if(token[++i]!='u') continue;
                var scalar=Hex(token.Slice(i+1,4)); i+=4;
                if(scalar is >=0xdc00 and <=0xdfff) throw DomainErrors.Error("InvalidTestSource");
                if(scalar is <0xd800 or >0xdbff) continue;
                if(i+6>=token.Length || token[i+1]!='\\' || token[i+2]!='u' || Hex(token.Slice(i+3,4)) is <0xdc00 or >0xdfff) throw DomainErrors.Error("InvalidTestSource");
                i+=6;
            }
        }
    }
    private static int Hex(ReadOnlySpan<byte> bytes)
    {
        var result=0;
        foreach(var b in bytes) result=(result<<4)+(b is >=(byte)'0' and <=(byte)'9' ? b-'0' : b is >=(byte)'a' and <=(byte)'f' ? b-'a'+10 : b-'A'+10);
        return result;
    }
}
