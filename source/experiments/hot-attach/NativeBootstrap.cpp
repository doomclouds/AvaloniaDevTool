#include <windows.h>
#include <mscoree.h>
#include <string>

// Minimal Windows/CoreCLR probe based on Snoop's GetCLRRuntimeHost approach.
// No work is performed in DllMain and no second CLR is initialized.
extern "C" __declspec(dllexport) DWORD WINAPI Bootstrap(void* argument)
{
    try
    {
        const std::wstring input(static_cast<const wchar_t*>(argument));
        const auto first = input.find(L'\n');
        const auto second = input.find(L'\n', first + 1);
        const auto third = input.find(L'\n', second + 1);
        if (first == std::wstring::npos || second == std::wstring::npos || third == std::wstring::npos)
            return E_INVALIDARG;
        const auto runtime = GetModuleHandleW(L"coreclr.dll");
        if (!runtime) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
        using GetHost = HRESULT(STDAPICALLTYPE*)(REFIID, IUnknown**);
        const auto getHost = reinterpret_cast<GetHost>(GetProcAddress(runtime, "GetCLRRuntimeHost"));
        if (!getHost) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        ICLRRuntimeHost* host = nullptr;
        auto hr = getHost(__uuidof(ICLRRuntimeHost), reinterpret_cast<IUnknown**>(&host));
        if (FAILED(hr)) return hr;
        DWORD result = 0;
        hr = host->ExecuteInDefaultAppDomain(input.substr(0, first).c_str(),
            input.substr(first + 1, second - first - 1).c_str(),
            input.substr(second + 1, third - second - 1).c_str(),
            input.substr(third + 1).c_str(), &result);
        host->Release();
        return FAILED(hr) ? hr : result;
    }
    catch (...) { return E_FAIL; }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
