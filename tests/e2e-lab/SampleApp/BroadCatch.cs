using System;

namespace SampleApp
{
    public class BroadCatch
    {
        public void Run()
        {
            try
            {
                Parse();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("failed: " + ex.Message);
            }
        }

        private void Parse()
        {
            throw new FormatException("bad");
        }
    }
}
