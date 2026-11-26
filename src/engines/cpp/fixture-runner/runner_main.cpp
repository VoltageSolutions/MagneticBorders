// fixture_runner CLI: runs every `.layout` file in a folder (or one file) and prints PASS/FAIL; `--print` shows final frames.
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "fixture.hpp"

namespace fs = std::filesystem;

namespace {
std::string slurp(const fs::path& p) {
    std::ifstream in(p, std::ios::binary);
    if (!in) throw std::runtime_error("cannot open " + p.string());
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc == 3 && std::string(argv[1]) == "--print") {
            const auto f = mb::fixture::parse(slurp(argv[2]));
            std::cout << mb::fixture::format(mb::fixture::run(f));
            return 0;
        }
        if (argc != 2) {
            std::cerr << "usage: fixture-runner [--print] <file-or-dir>\n";
            return 2;
        }
        std::vector<fs::path> files;
        if (fs::is_directory(argv[1])) {
            for (const auto& e : fs::directory_iterator(argv[1]))
                if (e.path().extension() == ".layout") files.push_back(e.path());
            std::ranges::sort(files);
        } else {
            files.emplace_back(argv[1]);
        }
        if (files.empty()) {
            std::cerr << "no fixtures found\n";
            return 1;
        }
        int failures = 0;
        for (const auto& p : files) {
            std::string why;
            try {
                const auto f = mb::fixture::parse(slurp(p));
                why = mb::fixture::check(f, mb::fixture::run(f));
            } catch (const std::exception& e) {
                why = e.what();
            }
            if (why.empty()) {
                std::cout << "PASS " << p.string() << "\n";
            } else {
                std::cout << "FAIL " << p.string() << ": " << why << "\n";
                ++failures;
            }
        }
        return failures ? 1 : 0;
    } catch (const std::exception& e) {
        std::cerr << e.what() << "\n";
        return 2;
    }
}
