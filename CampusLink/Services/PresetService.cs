using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CampusLink.Infrastructure;
using CampusLink.Models;
using Newtonsoft.Json;

namespace CampusLink.Services
{
    /// <summary>
    /// 学校预设管理：%APPDATA%\CampusLink\presets\ 下一个预设一个 json 文件。
    /// 内置「广州大学·大学城校区」预设，文件丢失时自动恢复。
    /// </summary>
    public class PresetService
    {
        public const string BuiltInId = "gzh-ucity";

        private readonly LogService _log;

        public PresetService(LogService log, string dataDirectory)
        {
            _log = log;
            Directory = Path.Combine(string.IsNullOrWhiteSpace(dataDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusLink")
                : dataDirectory, "presets");
            try { System.IO.Directory.CreateDirectory(Directory); } catch { }
        }

        public string Directory { get; }

        public static SchoolPreset CreateBuiltIn()
        {
            return new SchoolPreset
            {
                Id = BuiltInId,
                Name = "广州大学·大学城校区",
                Author = "Thomas455",
                PortalHost = "222.201.54.55",
                SelfServiceUrl = "http://222.201.54.23:8080/selfservice",
                DefaultPasswordTemplate = "{uid}@Gzhu",
                BuiltIn = true
            };
        }

        public List<SchoolPreset> LoadAll()
        {
            var list = new List<SchoolPreset>();
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        var json = File.ReadAllText(file, Encoding.UTF8);
                        var preset = JsonConvert.DeserializeObject<SchoolPreset>(json);
                        if (preset == null || string.IsNullOrWhiteSpace(preset.Name)) continue;
                        if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Path.GetFileNameWithoutExtension(file);
                        preset.FilePath = file;
                        list.Add(preset);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("读取预设失败：" + Path.GetFileName(file) + " -> " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("读取预设目录失败：" + ex.Message);
            }

            // 内置预设必须在
            if (list.All(p => !string.Equals(p.Id, BuiltInId, StringComparison.OrdinalIgnoreCase)))
            {
                var builtIn = CreateBuiltIn();
                Save(builtIn);
                builtIn.FilePath = PathFor(builtIn);
                list.Insert(0, builtIn);
            }

            return list.OrderByDescending(p => p.BuiltIn).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public SchoolPreset Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return LoadAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public void Save(SchoolPreset preset)
        {
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
            {
                throw new ArgumentException("预设名称不能为空");
            }

            if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Slug(preset.Name);
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(preset);
            var copy = preset.Clone();
            copy.FilePath = null;
            File.WriteAllText(path, JsonConvert.SerializeObject(copy, Formatting.Indented), new UTF8Encoding(false));
            preset.FilePath = path;
            _log?.Info("预设已保存：" + preset.Name);
        }

        public void Delete(SchoolPreset preset)
        {
            if (preset == null) return;
            var path = preset.FilePath;
            if (string.IsNullOrWhiteSpace(path)) path = PathFor(preset);
            if (File.Exists(path)) File.Delete(path);
            _log?.Info("预设已删除：" + preset.Name);
        }

        public void Export(SchoolPreset preset, string targetFile)
        {
            if (preset == null || string.IsNullOrWhiteSpace(targetFile)) return;
            var copy = preset.Clone();
            copy.FilePath = null;
            File.WriteAllText(targetFile, JsonConvert.SerializeObject(copy, Formatting.Indented), new UTF8Encoding(false));
        }

        public SchoolPreset Import(string file)
        {
            var json = File.ReadAllText(file, Encoding.UTF8);
            var preset = JsonConvert.DeserializeObject<SchoolPreset>(json);
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
            {
                throw new InvalidDataException("这不是有效的学校预设文件");
            }

            // Id 冲突时生成新的，避免覆盖已有预设
            var existing = LoadAll();
            if (string.IsNullOrWhiteSpace(preset.Id) || existing.Any(p => p.Id == preset.Id))
            {
                preset.Id = Slug(preset.Name) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }
            preset.BuiltIn = false;
            Save(preset);
            return preset;
        }

        /// <summary>生成可作文件名的 slug（保留中文，去掉非法字符）。</summary>
        public static string Slug(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder();
            foreach (var ch in (name ?? "").Trim())
            {
                builder.Append(invalid.Contains(ch) || ch == ' ' ? '-' : ch);
            }
            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? "preset-" + Guid.NewGuid().ToString("N").Substring(0, 8) : slug;
        }

        private string PathFor(SchoolPreset preset)
        {
            var id = string.IsNullOrWhiteSpace(preset.Id) ? Slug(preset.Name) : preset.Id;
            foreach (var ch in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(ch, '-');
            }
            return Path.Combine(Directory, id + ".json");
        }
    }
}
