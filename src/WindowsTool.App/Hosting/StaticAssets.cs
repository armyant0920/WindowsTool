using Microsoft.Extensions.FileProviders;

namespace WindowsTool.App.Hosting;

public static class StaticAssets
{
    /// <summary>
    /// Development 且磁碟上有 wwwroot 時直接讀檔（改了重新整理就生效）；
    /// 其他情況一律讀 exe 內嵌的檔案。
    /// </summary>
    public static IFileProvider CreateFileProvider(IWebHostEnvironment env)
    {
        if (env.IsDevelopment() && Directory.Exists(env.WebRootPath))
        {
            return new PhysicalFileProvider(env.WebRootPath);
        }

        return new ManifestEmbeddedFileProvider(typeof(StaticAssets).Assembly, "wwwroot");
    }
}
