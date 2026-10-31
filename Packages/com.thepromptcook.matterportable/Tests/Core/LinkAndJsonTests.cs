using System;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class MatterportLinkTests
    {
        [TestCase("https://discover.matterport.com/space/rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://my.matterport.com/show/?m=rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://my.matterport.com/show?m=rGMMnZDh3Tf&play=1&qs=1", "rGMMnZDh3Tf")]
        [TestCase("my.matterport.com/show/?m=rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://my.matterport.com/models/rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://matterport.com/discover/space/rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://my.matterport.com/work?m=rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("https://example.com/tour#m=rGMMnZDh3Tf", "rGMMnZDh3Tf")]
        [TestCase("  rGMMnZDh3Tf  ", "rGMMnZDh3Tf")]
        [TestCase("\"https://discover.matterport.com/space/rGMMnZDh3Tf\"", "rGMMnZDh3Tf")]
        public void ParsesCommonLinkForms(string input, string expected)
        {
            Assert.IsTrue(MatterportLink.TryParseModelId(input, out var id), input);
            Assert.AreEqual(expected, id);
        }

        [TestCase("")]
        [TestCase("hello world")]
        [TestCase("https://matterport.com/")]
        [TestCase("https://discover.matterport.com/")]
        public void RejectsLinksWithoutAnId(string input)
        {
            Assert.IsFalse(MatterportLink.TryParseModelId(input, out _));
            Assert.Throws<ArgumentException>(() => MatterportLink.ParseModelId(input));
        }
    }

    public class JsonTests
    {
        [Test]
        public void RoundTripsNestedValues()
        {
            var src = "{\"a\":1.5,\"b\":[true,false,null,\"x\\n\\\"y\\\"\"],\"c\":{\"d\":-2e3,\"e\":\"\\u00e9\"}}";
            var j = JsonValue.Parse(src);
            Assert.AreEqual(1.5, j["a"].AsDouble());
            Assert.IsTrue(j["b"][0].AsBool());
            Assert.IsTrue(j["b"][2].IsNull);
            Assert.AreEqual("x\n\"y\"", j["b"][3].AsString());
            Assert.AreEqual(-2000, j["c"]["d"].AsDouble());
            Assert.AreEqual("é", j["c"]["e"].AsString());
            var again = JsonValue.Parse(j.ToJson(true));
            Assert.AreEqual(j.ToJson(false), again.ToJson(false));
        }

        [Test]
        public void MissingMembersChainToNull()
        {
            var j = JsonValue.Parse("{\"data\":{}}");
            Assert.IsTrue(j["data"]["model"]["locations"][3]["id"].IsNull);
            Assert.AreEqual(0, j["data"]["model"]["locations"].Count);
            Assert.AreEqual("fallback", j["nope"].AsString("fallback"));
        }

        [Test]
        public void ShortFloatFormatting()
        {
            var j = JsonValue.NewObject().Set("f", 0.1f);
            Assert.AreEqual("{\"f\":0.1}", j.ToJson(false));
        }

        [Test]
        public void ReportsErrorPosition()
        {
            Assert.IsFalse(JsonValue.TryParse("{\"a\":\n  [1, 2,, 3]}", out _, out var error));
            StringAssert.Contains("line 2", error);
        }
    }
}
