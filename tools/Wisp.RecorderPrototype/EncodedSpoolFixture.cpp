#include "EncodedSpool.h"
#include <cstdio>
#include <cwchar>

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::puts("Wisp encoded spool contracts. --self-test, or --file-contracts --parent <isolated existing local directory>. Default opens no files.");
        return 0;
    }
    const bool cpu = argc == 2 && wcscmp(argv[1], L"--self-test") == 0;
    const bool files = argc == 4 && wcscmp(argv[1], L"--file-contracts") == 0 && wcscmp(argv[2], L"--parent") == 0;
    if (!cpu && !files) { std::puts("{\"completed\":false,\"reason\":\"invalid_arguments\"}"); return 2; }
    try
    {
        const int count = cpu ? recorder::spool::RunSpoolCpuContracts() : recorder::spool::RunSpoolFileContracts(argv[3]);
        std::printf("{\"completed\":true,\"contracts\":%d,\"fileContracts\":%s,\"capture\":false}\n", count, files ? "true" : "false");
        return 0;
    }
    catch (const recorder::spool::ContractFailure& failure)
    {
        std::printf("{\"completed\":false,\"reason\":\"spool_contract_failed\",\"checkIndex\":%d}\n", failure.index);
        return 1;
    }
    catch (...) { std::puts("{\"completed\":false,\"reason\":\"spool_contract_setup_failed\"}"); return 1; }
}
