#pragma once

#include <filesystem>
#include <set>
#include <string>

namespace tt
{
// The physical drives that disk work in a folder reaches, such as disk 0 or
// the server dw. Two jobs that share no drive can run at the same time
// without slowing each other down.
struct Drives
{
    std::set<std::string> names;

    // Finds the drives of `folder`, or of its nearest existing parent, through
    // any link in its path. A folder the engine cannot identify is a drive of
    // its own. Blocks, for long on a share that does not answer.
    static Drives Of(std::filesystem::path const& folder);
};
}
