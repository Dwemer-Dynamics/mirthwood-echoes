using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Lowscope.Saving;
using Lowscope.Saving.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Menus;

namespace Mirthwood.Echoes
{
    // Installed only for a Steam process explicitly started with the harness argument.
    internal sealed class IsolatedGameRun
    {
        internal static IsolatedGameRun Active;
        public readonly string Root,Id;
        public readonly int SourceSlot;
        public readonly string Preference;
        public string TalkAddress {get;private set;}
        public bool PrivateDiagnostics {get;private set;}
        IsolatedGameRun(string root,string id,int source){Root=root;Id=id;SourceSlot=source;Preference="Mirthwood.Echoes.Harness."+id;}
        public static bool Requested=>Environment.GetCommandLineArgs().Contains("-echoes-harness");
        [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="PostMessageW")]
        static extern bool PostMessage(IntPtr window,uint message,IntPtr key,IntPtr detail);
        public static bool PostReturn()=>PostTextKey(13,0x1c);
        public static bool PostLetterF()=>PostTextKey('F',0x21);
        static bool PostTextKey(int key,int scan)
        {
            if(Active==null||!Active.PathsMatch()||Application.platform!=RuntimePlatform.WindowsPlayer)return false;
            using(var process=System.Diagnostics.Process.GetCurrentProcess()){
                var window=process.MainWindowHandle;if(window==IntPtr.Zero)return false;
                int flags=(scan<<16)|1;
                return PostMessage(window,0x100,(IntPtr)key,(IntPtr)flags)
                    &&PostMessage(window,0x101,(IntPtr)key,(IntPtr)(flags|unchecked((int)0xc0000000)));
            }
        }
        public static bool PostTalkOrEscape(bool talk,bool down)=>PostNavigationKey(talk?0x77:0x1b,talk?0x42:1,down);
        internal static bool PostGroupKey(bool down)=>PostNavigationKey('T',0x14,down);
        internal static bool PostListenKey()=>PostTextKey(9,0x0f);
        static bool PostNavigationKey(int key,int scan,bool down)
        {
            if(Active==null||!Active.PathsMatch()||Application.platform!=RuntimePlatform.WindowsPlayer)return false;
            using(var process=System.Diagnostics.Process.GetCurrentProcess()){
                int flags=(scan<<16)|1;
                if(!down)flags|=unchecked((int)0xc0000000);
                return process.MainWindowHandle!=IntPtr.Zero&&PostMessage(process.MainWindowHandle,down?0x100u:0x101u,(IntPtr)key,(IntPtr)flags);
            }
        }
        public static IsolatedGameRun Attach(Harmony harmony)
        {
            string[] args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"-echoes-harness");
            if(index<0)return null;
            if(index+1>=args.Length||!Guid.TryParseExact(args[index+1],"N",out var token))throw new InvalidDataException("Missing isolated run identity.");
            string id=token.ToString("N");
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes","Harness",id);
            foreach(var d in new[]{root,Path.GetDirectoryName(root),Path.Combine(root,"SaveData")})
                if((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0)throw new IOException("The test directory is a filesystem link.");
            var manifest=JObject.Parse(File.ReadAllText(Path.Combine(root,"harness.json")));
            var copy=Path.Combine(root,"SaveData","Slot0.savegame");
            using(var input=File.OpenRead(copy))using(var sha=SHA256.Create()){
                var digest=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");
                if((string)manifest["id"]!=id||!string.Equals(digest,(string)manifest["sha256"],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The test save does not match its receipt.");
            }
            int slot=(int)manifest["sourceSlot"];if(slot<0||slot>299)throw new InvalidDataException("Invalid source slot.");
            Active=new IsolatedGameRun(root,id,slot);
            if((int?)manifest["talkPort"]==17864)Active.TalkAddress="http://127.0.0.1:17864/api/";
            Active.PrivateDiagnostics=Active.TalkAddress!=null&&(bool?)manifest["privateDiagnostics"]==true;
            harmony.Patch(AccessTools.PropertyGetter(typeof(TestSaveFolder),nameof(TestSaveFolder.Root)),prefix:new HarmonyMethod(typeof(IsolatedGameRun),nameof(RootForTest)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(TestSaveFolder),nameof(TestSaveFolder.LastUsedSlotKey)),prefix:new HarmonyMethod(typeof(IsolatedGameRun),nameof(PrefForTest)));
            if(!Active.PathsMatch())throw new InvalidDataException("The native save paths did not switch to the test copy.");
            return Active;
        }
        static bool RootForTest(ref string __result){__result=Active.Root;return false;}
        static bool PrefForTest(ref string __result){__result=Active.Preference;return false;}
        public bool PathsMatch()=>string.Equals(Path.GetFullPath(SaveFileUtility.SaveFolderPath),Path.Combine(Root,"SaveData"),StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(SaveFileUtility.TempFolderPath).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            && TestSaveFolder.LastUsedSlotKey==Preference;
        public IEnumerator Load()
        {
            TitleScreenController title=null;float deadline=Time.realtimeSinceStartup+90;
            while(title==null&&Time.realtimeSinceStartup<deadline){title=UnityEngine.Object.FindObjectOfType<TitleScreenController>();yield return null;}
            yield return new WaitForSecondsRealtime(1.5f);
            if(title==null||!PathsMatch()||!SaveMaster.IsSlotUsed(0)){UnityEngine.Debug.LogError("Echoes isolated save could not be loaded.");yield break;}
            PlayerPrefs.SetInt(Preference,0);
            if(SaveMaster.GetLastUsedValidSlot()==0)title.Continue();
        }
        public void CheckQuit()
        {
            if(!File.Exists(Path.Combine(Root,"quit.request")))return;
            PlayerPrefs.DeleteKey(Preference);PlayerPrefs.Save();Application.Quit();
        }
    }
}
