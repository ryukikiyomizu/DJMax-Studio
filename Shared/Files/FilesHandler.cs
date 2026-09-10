using System;
using System.Collections.Generic;
using System.Linq;

namespace DJMaxEditor.Files
{
    internal abstract class FilesHandler<Type> where Type : IFile
    {
        protected IList<Type> _handlers;

        public FilesHandler()
        {
            _handlers = new List<Type>();
        }

        public void Register(Type handler)
        {
            _handlers.Add(handler);
        }

        public string GetFilter()
        {
            return String.Join("|", _handlers.Select(x =>
                $"{x.GetDescription()}|{String.Join(";", ExtensionsOf(x).Select(ext => "*." + ext))}"));
        }

        public string GetDefaultExtension()
        {
            var firstEntry = _handlers.FirstOrDefault();
            if (firstEntry == null)
            {
                return String.Empty;
            }
            var extension = ExtensionsOf(firstEntry).FirstOrDefault();
            return extension == null ? String.Empty : $"*.{extension}";
        }

        public Type GetHandlerForExtension(string extension)
        {
            string normalized = (extension ?? String.Empty).TrimStart('.').ToLowerInvariant();
            return _handlers.FirstOrDefault(x =>
                ExtensionsOf(x).Any(ext => String.Equals(ext, normalized, StringComparison.OrdinalIgnoreCase)));
        }

        public Type GetHandlerForFilterIndex(int filterIndex) 
        {
            int index = filterIndex - 1;
            if ((index < 0) || (index > _handlers.Count - 1)) 
            {
                return default;
            }
            // Todo: fix this for the case of multiple extension for the same Handler
            return _handlers[filterIndex - 1];
        }

        private static IEnumerable<string> ExtensionsOf(Type handler)
        {
            return (handler.GetExtension() ?? String.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
                .Where(x => x.Length > 0);
        }
    }
}
