namespace Orbit;

internal static class LoginAutofill
{
    public static bool LoginPage(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort && url.Host is "www.roblox.com" or "roblox.com" && url.AbsolutePath.TrimEnd('/').Equals("/login", StringComparison.OrdinalIgnoreCase);
    public static string Script(SavedLogin login) => """
        (()=>{
          if(window.top!==window.self||location.protocol!=='https:'||!['www.roblox.com','roblox.com'].includes(location.hostname)||location.port&&location.port!=='443'||location.pathname.replace(/\/$/,'').toLowerCase()!=='/login')return {filled:false,reason:'wrong-page'};
          const u=document.getElementById('login-username'),p=document.getElementById('login-password');
          if(document.readyState==='loading')return {filled:false,reason:'not-ready'};
          if(!u||!p||u.tagName!=='INPUT'||p.tagName!=='INPUT'||p.type!=='password')return {filled:false,reason:'not-ready'};
          const saved=__SAVED_LOGIN__;
          const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;
          for(const [el,value]of [[u,saved.username],[p,saved.password]]){setter.call(el,value);el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
          return {filled:true};
        })()
        """.Replace("__SAVED_LOGIN__", Json.Write(new { username = login.Username, password = login.Password }), StringComparison.Ordinal);
    public static string SubmitScript(SavedLogin login) => """
        (()=>{
          if(window.top!==window.self||location.protocol!=='https:'||!['www.roblox.com','roblox.com'].includes(location.hostname)||location.port&&location.port!=='443'||location.pathname.replace(/\/$/,'').toLowerCase()!=='/login')return false;
          const saved=__SAVED_LOGIN__,u=document.getElementById('login-username'),p=document.getElementById('login-password'),button=document.getElementById('login-button');
          if(!u||!p||p.type!=='password'||u.value!==saved.username||p.value!==saved.password||!button||button.disabled)return false;
          button.click();return true;
        })()
        """.Replace("__SAVED_LOGIN__", Json.Write(new { username = login.Username, password = login.Password }), StringComparison.Ordinal);
}
