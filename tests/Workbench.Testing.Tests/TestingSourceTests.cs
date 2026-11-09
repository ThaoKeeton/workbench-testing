using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Sources;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingSourceTests
{
    [Theory]
    [InlineData(TestSourceKind.Requirements, "builtin.requirements-json")]
    [InlineData(TestSourceKind.Figma, "builtin.figma-snapshot-json")]
    public async Task Bundled_local_payloads_preserve_original_BOM_bytes_hash_and_exact_criterion_traces(TestSourceKind kind, string providerId)
    {
        using var disk = new TestDirectory();
        ITestSourceProvider provider = kind == TestSourceKind.Requirements ? new RequirementSourceProvider() : new FigmaSnapshotProvider();
        var original = SyntheticTestingExamples.Read(kind);
        var bom = new byte[] { 239, 187, 191 }.Concat(original).ToArray();
        var draft = provider.Parse(bom);
        Assert.Equal(providerId, draft.ProviderId);
        Assert.Equal("1", draft.ProviderVersion);
        Assert.Equal(kind, draft.Kind);
        Assert.Equal(bom, draft.OriginalBytes);
        bom[^1] = 0;
        Assert.Equal((byte)'\n', draft.OriginalBytes[^1]);
        var store = new ProjectTestStore(disk.User, "default");
        var saved = await store.PublishSourceAsync(PluginTestingContextTests.Owner, draft);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(draft.OriginalBytes)), saved.OriginalSha256);
        ITestCaseGenerator generator = new RuleTestCaseGenerator();
        var cases = generator.Generate([saved]);
        Assert.Equal(saved.Items.Sum(x => x.AcceptanceCriteria.Count), cases.Cases.Count);
        foreach (var item in saved.Items)
        for (var index = 0; index < item.AcceptanceCriteria.Count; index++)
        {
            var one = Assert.Single(cases.Cases, c => c.Traces.Single().Locator == item.Locator && c.Traces.Single().CriterionIndex == index);
            Assert.Equal(item.Title, one.Title);
            Assert.Equal(new TestTrace(saved.Id, saved.Revision, item.Locator, index), one.Traces.Single());
            Assert.All(one.Steps, s => Assert.Equal(one.Traces, s.Traces));
            if (item.VirtualScenario is null)
            {
                Assert.Null(Assert.Single(one.Steps).Action);
                Assert.Equal(item.AcceptanceCriteria[index], one.Steps[0].Expected);
            }
            else Assert.All(one.Steps, s => Assert.NotNull(s.Action));
        }
        var first = await store.CreateCaseSetAsync(PluginTestingContextTests.Owner, cases);
        var second = await store.CreateCaseSetAsync(PluginTestingContextTests.Owner, generator.Generate([saved]));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await store.ListCaseSetsAsync()).Count);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"documentId\":\"x\",\"title\":\"x\",\"requirements\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"documentId\":\"x\",\"title\":\"x\",\"requirements\":[],\"unknown\":1}")]
    [InlineData("{\"schemaVersion\":2,\"documentId\":\"x\",\"title\":\"x\",\"requirements\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"documentId\":\"x\",\"title\":\"\\uD800\",\"requirements\":[]}")]
    public void Malformed_or_unsupported_local_schema_refuses_without_replacement(string json)
        => Code("InvalidTestSource", () => new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Parser_refuses_unknown_nested_fields_duplicates_missing_scenario_invalid_utf8_and_double_BOM()
    {
        var valid = Json();
        foreach (var json in new[]
        {
            valid.Replace("\"description\":\"\"", "\"description\":\"\",\"extra\":null", StringComparison.Ordinal),
            valid.Replace("\"id\":\"../literal\"", "\"id\":\"../literal\",\"id\":\"other\"", StringComparison.Ordinal),
            valid.Replace(",\"virtualScenario\":null", "", StringComparison.Ordinal),
            valid.Replace("\"virtualScenario\":null", "\"virtualScenario\":\"taskformpositive\"", StringComparison.Ordinal),
            valid.Replace("\"acceptanceCriteria\":[\"=criterion\"]", "\"acceptanceCriteria\":[\"\\uDC00\"]", StringComparison.Ordinal)
        }) Code("InvalidTestSource", () => new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(json)));
        var bytes = Encoding.UTF8.GetBytes(valid);
        Code("InvalidTestSource", () => new RequirementSourceProvider().Parse(new byte[] { 0xff }.Concat(bytes).ToArray()));
        Code("InvalidTestSource", () => new RequirementSourceProvider().Parse(new byte[] { 239,187,191,239,187,191 }.Concat(bytes).ToArray()));
        var twice = Json(items: 2, duplicate: true);
        Code("InvalidTestSource", () => new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(twice)));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("items")]
    [InlineData("criteria")]
    [InlineData("title")]
    [InlineData("criterion")]
    public void Parser_enforces_actual_caps_before_publication(string cap)
    {
        var json = cap switch
        {
            "items" => Json(items: 257), "criteria" => Json(criteria: 17),
            "title" => Json(title: new string('a', 201)), "criterion" => Json(criterion: new string('a', 4097)),
            _ => new string(' ', 1024 * 1024 + 1)
        };
        Code("StoreLimitExceeded", () => new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Source_read_is_bounded_even_for_nonseekable_stream_and_generator_refuses_over_200_without_dropping_criteria()
    {
        using var stream = new NonSeekingBytes(new byte[1024 * 1024 + 1]);
        var error = await Assert.ThrowsAsync<PluginTestingException>(() => TestSourceInput.ReadAsync(stream));
        Assert.Equal("StoreLimitExceeded", error.ErrorCode);
        var provider = new RequirementSourceProvider();
        var draft = provider.Parse(Encoding.UTF8.GetBytes(Json(items: 201)));
        var source = new TestSourceSnapshot(new string('1',32), "default", PluginTestingContextTests.Owner, DateTimeOffset.UtcNow,
            new string('a',64), draft.Kind, draft.ProviderId, draft.ProviderVersion, draft.DocumentId, draft.Title, draft.OriginalBytes, new string('b',64), draft.Items);
        Code("StoreLimitExceeded", () => new RuleTestCaseGenerator().Generate([source]));
    }

    [Fact]
    public async Task CSV_actual_bytes_quote_every_field_and_prefix_every_text_cell_preserving_exact_store_values()
    {
        using var disk = new TestDirectory();
        var store = new ProjectTestStore(disk.User,"default");
        var source = await store.PublishSourceAsync(PluginTestingContextTests.Owner, new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(Json())));
        var trace = new TestTrace(source.Id, source.Revision, "../literal", 0);
        var original = "=SUM(1,2)\r\n\"fullwidth ＋\" 🧪";
        var set = await store.CreateCaseSetAsync(PluginTestingContextTests.Owner, new("CSV", [new("12345678901234567890", "@setup", [trace], [new(original,"\t-formula",[trace],null)])]));
        var csv = new CsvTestCaseExporter().Export(set);
        Assert.Equal(new byte[] {239,187,191}, csv[..3]);
        var text = new UTF8Encoding(false,true).GetString(csv[3..]);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        var rows = ParseCsv(text);
        var header = rows[0];
        Assert.Equal(3,rows.Count);
        var row = rows[1];
        Assert.All(rows, r => Assert.Equal(header.Length,r.Length));
        string Cell(string name) => row[Array.IndexOf(header,name)];
        Assert.Equal("'" + original, Cell("instruction"));
        Assert.Equal("'12345678901234567890", Cell("caseTitle"));
        Assert.Equal("'@setup", Cell("prerequisites"));
        Assert.Equal("'" + set.Cases[0].Id, Cell("caseId"));
        Assert.Equal("'" + source.Revision, Cell("sourceRevision"));
        Assert.Equal("'../literal", Cell("sourceLocator"));
        Assert.Equal("1", Cell("stepOrdinal")); Assert.Equal("0",Cell("criterionIndex")); Assert.Equal("1",Cell("traceOrdinal"));
        foreach(var name in header.Where(x => x is not ("stepOrdinal" or "criterionIndex" or "traceOrdinal"))) Assert.StartsWith("'",Cell(name),StringComparison.Ordinal);
        Assert.Equal("'case",Cell("traceScope"));
        Assert.Equal("'step",rows[2][Array.IndexOf(header,"traceScope")]);
        Assert.Equal(original,(await store.ReadCaseSetAsync(set.Id)).Cases[0].Steps[0].Instruction);
        // Independent wire check: every field starts with a quote, including numerics; embedded CRLF stays inside a cell.
        Assert.Contains("\r\n\"\"fullwidth ＋\"\" 🧪\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CSV_preserves_case_and_step_scopes_with_numeric_text_identifiers_and_formula_prefixes()
    {
        var first=new TestTrace(new string('1',32),new string('2',64),"12345678901234567890",0);
        var second=new TestTrace(new string('3',32),new string('4',64),"=locator",1);
        var third=new TestTrace(new string('5',32),new string('6',64),"＋fullwidth",2);
        var set=new TestCaseSetSnapshot(new string('7',32),"default",PluginTestingContextTests.Owner,DateTimeOffset.UtcNow,new string('8',64),"CSV",
            [new(new string('9',32),"+case","-setup",[first,third],[new(new string('0',32),"@instruction","\t=expected",[first,second],null)])]);
        var rows=ParseCsv(Encoding.UTF8.GetString(new CsvTestCaseExporter().Export(set)[3..]));
        var headers=rows[0];Assert.Equal(5,rows.Count);
        string Cell(string[] row,string name)=>row[System.Array.IndexOf(headers,name)];
        Assert.Equal(new[]{"'case","'case","'step","'step"},rows.Skip(1).Select(r=>Cell(r,"traceScope")));
        Assert.Equal(new[]{"1","2","1","2"},rows.Skip(1).Select(r=>Cell(r,"traceOrdinal")));
        Assert.Equal(new[]{"'12345678901234567890","'＋fullwidth","'12345678901234567890","'=locator"},rows.Skip(1).Select(r=>Cell(r,"sourceLocator")));
        Assert.All(rows.Skip(1),r=> { Assert.Equal("'"+new string('9',32),Cell(r,"caseId"));Assert.Equal("'"+new string('0',32),Cell(r,"stepId")); });
    }

    [Fact]
    public void CSV_preserves_trace_order_and_duplicate_multiplicity_with_one_based_ordinals_for_every_step()
    {
        var a=new TestTrace(new string('1',32),new string('2',64),"=A",0);
        var b=new TestTrace(new string('3',32),new string('4',64),"../B",1);
        TestTrace[] caseTraces=[a,a,b], stepTraces=[b,a,b];
        var set=new TestCaseSetSnapshot(new string('5',32),"default",PluginTestingContextTests.Owner,DateTimeOffset.UtcNow,new string('6',64),"CSV",
            [new(new string('7',32),"case","setup",caseTraces,
                [new(new string('8',32),"first","expected",stepTraces,null),new(new string('9',32),"second","expected",stepTraces,null)])]);
        var rows=ParseCsv(new UTF8Encoding(false,true).GetString(new CsvTestCaseExporter().Export(set)[3..]));
        Assert.Equal(13,rows.Count);
        string Cell(string[] row,string name)=>row[Array.IndexOf(rows[0],name)];
        for(var stepIndex=0;stepIndex<2;stepIndex++)
        {
            var group=rows.Skip(1+stepIndex*6).Take(6).ToArray();
            Assert.Equal(new[]{"'case","'case","'case","'step","'step","'step"},group.Select(r=>Cell(r,"traceScope")));
            Assert.Equal(new[]{"1","2","3","1","2","3"},group.Select(r=>Cell(r,"traceOrdinal")));
            Assert.Equal(new[]{"'=A","'=A","'../B","'../B","'=A","'../B"},group.Select(r=>Cell(r,"sourceLocator")));
            Assert.Equal(new[]{"0","0","1","1","0","1"},group.Select(r=>Cell(r,"criterionIndex")));
            Assert.Equal(new[]{a,a,b,b,a,b}.Select(t=>"'"+t.SnapshotId),group.Select(r=>Cell(r,"sourceId")));
            Assert.Equal(new[]{a,a,b,b,a,b}.Select(t=>"'"+t.SnapshotRevision),group.Select(r=>Cell(r,"sourceRevision")));
            Assert.All(group,r=> { Assert.Equal((stepIndex+1).ToString(System.Globalization.CultureInfo.InvariantCulture),Cell(r,"stepOrdinal"));Assert.Equal("'"+set.Cases[0].Steps[stepIndex].Id,Cell(r,"stepId")); });
        }
        Assert.Equal(new[]{a,a,b},caseTraces);Assert.Equal(new[]{b,a,b},stepTraces);
    }

    [Fact]
    public async Task Valid_whitespace_description_stays_exact_in_source_and_manual_instruction_uses_the_item_title()
    {
        using var disk=new TestDirectory();var store=new ProjectTestStore(disk.User,"default");
        var json=Json().Replace("\"description\":\"\"","\"description\":\" \\r\\n\\t\"",StringComparison.Ordinal);
        var source=await store.PublishSourceAsync(PluginTestingContextTests.Owner,new RequirementSourceProvider().Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(" \r\n\t",source.Items[0].Description);
        var generated=new RuleTestCaseGenerator().Generate([source]);
        Assert.Equal(source.Items[0].Title,generated.Cases[0].Steps[0].Instruction);
        var set=await store.CreateCaseSetAsync(PluginTestingContextTests.Owner,generated);
        Assert.Null(set.Cases[0].Steps[0].Action);Assert.Equal("=criterion",set.Cases[0].Steps[0].Expected);
        Assert.Equal(" \r\n\t",(await store.ReadSourceAsync(source.Id)).Items[0].Description);
    }

    internal static string Json(int items=1,int criteria=1,string title="Local task 🧪",string criterion="=criterion",bool duplicate=false)
        => JsonSerializer.Serialize(new { schemaVersion=1,documentId="synthetic-local",title,requirements=Enumerable.Range(0,items).Select(i=>new
        { id=duplicate || items==1 ? "../literal" : $"item-{i}",title,description="",acceptanceCriteria=Enumerable.Repeat(criterion,criteria),virtualScenario=(string?)null }) });
    private static void Code(string expected, Action action) => Assert.Equal(expected,Assert.Throws<PluginTestingException>(action).ErrorCode);

    internal static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); bool quoted=false,afterQuote=false;
        for(var i=0;i<text.Length;i++)
        {
            var c=text[i];
            if(quoted)
            {
                if(c=='"') { if(i+1<text.Length && text[i+1]=='"') { field.Append('"'); i++; } else { quoted=false;afterQuote=true; } }
                else field.Append(c);
            }
            else if(!afterQuote) { Assert.Equal('"',c);quoted=true; }
            else if(c==',') { fields.Add(field.ToString());field.Clear();afterQuote=false; }
            else { Assert.Equal('\r',c);Assert.Equal('\n',text[++i]);fields.Add(field.ToString());field.Clear();rows.Add(fields.ToArray());fields.Clear();afterQuote=false; }
        }
        Assert.False(quoted);Assert.False(afterQuote);Assert.Empty(fields);return rows;
    }
    private sealed class NonSeekingBytes(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; public override long Length => throw new NotSupportedException(); }
}
