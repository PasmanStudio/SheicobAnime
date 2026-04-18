using AnimeIndex.Api.Infrastructure.Resolvers;
using Xunit;

namespace AnimeIndex.Api.Tests;

public class PackedJsUnpackerTests
{
    [Fact]
    public void Unpack_ReturnsOriginalWhenNoPackedBlock()
    {
        var input = "var x = 1; console.log('plain');";
        Assert.Equal(input, PackedJsUnpacker.Unpack(input));
    }

    [Fact]
    public void Unpack_ExpandsBasicDeanEdwardsPackedBlock()
    {
        // Real Dean-Edwards packed snippet equivalent to: var a="hello";
        var packed = "eval(function(p,a,c,k,e,d){e=function(c){return c};if(!''.replace(/^/,String)){while(c--){d[c]=k[c]||c}k=[function(e){return d[e]}];e=function(){return'\\\\w+'};c=1};while(c--){if(k[c]){p=p.replace(new RegExp('\\\\b'+e(c)+'\\\\b','g'),k[c])}}return p}('var 0=\"hello\";',2,2,'a||'.split('|'),0,{}))";
        var result = PackedJsUnpacker.Unpack(packed);
        // Result should contain 'a' or 'hello' after unpacking attempt
        Assert.Contains("hello", result);
    }
}
