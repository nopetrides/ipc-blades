using NUnit.Framework;

namespace BladeContact.Tests
{
    public sealed class BladeFeatureTypeTests
    {
        [Test]
        public void SharpEdge_IsDistinctFromProfileFeatureEdge()
        {
            Assert.AreNotEqual(BladeFeatureType.SharpEdge, BladeFeatureType.ProfileFeatureEdge);
        }
    }
}
