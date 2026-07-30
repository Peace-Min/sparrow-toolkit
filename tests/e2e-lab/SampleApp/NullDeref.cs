using System.Collections.Generic;
using System.Linq;

namespace SampleApp
{
    public class NullDeref
    {
        public int GetValue(List<Item> items, int id)
        {
            var node = items.FirstOrDefault(n => n.Id == id);
            return node.Value;
        }
    }

    public class Item
    {
        public int Id;
        public int Value;
    }
}
