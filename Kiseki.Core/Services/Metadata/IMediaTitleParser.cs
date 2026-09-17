using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public interface IMediaTitleParser
{
    ParsedMediaTitle Parse(string rawTitle);
}

