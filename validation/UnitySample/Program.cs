using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class MonoBehaviour { }
    public class ContextMenuAttribute:Attribute { public ContextMenuAttribute(string text) { } }
    public static class Debug { public static List<string> Messages=new List<string>(); public static void Log(object value){Messages.Add(value.ToString());} }
}
class Program
{
    static int Main()
    {
        new SpatialQueryExample().Run();
        if(UnityEngine.Debug.Messages.Count!=3 || UnityEngine.Debug.Messages[0]!="Initial sphere hits: 1" || UnityEngine.Debug.Messages[1]!="Hits after move: 0")throw new Exception("Imported sample behavior changed.");
        Console.WriteLine("PASS UPM sample source against independent .NET Standard 2.1 runtime assembly");return 0;
    }
}
