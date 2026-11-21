#include "shell/messengers.h"

#include <iostream>
#include <stdexcept>

using namespace atrium;

int main()
{
    try {
        auto require = [](bool condition, const char* message) {
            if (!condition)
                throw std::runtime_error(message);
        };
        require(MessengerOfProgram(L"D:\\Download App\\Tencent\\Weixin\\Weixin.exe") == Messenger::WeChat, "WeChat 4 by its program");
        require(MessengerOfProgram(L"c:\\program files\\wxwork\\WXWork.exe") == Messenger::WeCom, "WeCom by its program");
        require(MessengerOfProgram(L"D:\\Tencent\\WXWorkLocalPro\\WXWorkLocalPro.exe") == Messenger::WeCom, "WeCom private edition");
        require(MessengerOfProgram(L"DingTalk.exe") == Messenger::DingTalk, "DingTalk by its file name");
        require(MessengerOfProgram(L"C:\\Windows\\explorer.exe") == Messenger::None, "other programs are none of them");
        require(MessengerOfNotice(L"windows", L"QQ", L"QQ") == Messenger::QQ, "QQ's Windows notification (AUMID QQ)");
        require(MessengerOfNotice(L"windows", L"\u9489\u9489", L"") == Messenger::DingTalk, "a notification by its shown name");
        require(MessengerOfNotice(L"wechat", L"WeChat", L"") == Messenger::WeChat, "the WeChat reader's card");
        require(MessengerOfNotice(L"windows", L"Mail", L"microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail") == Messenger::None,
                "other apps' notifications follow notifyListener");
        require(MessengerOfNotice(L"windows", L"QQ Music", L"QQMusic") == Messenger::None, "a name only containing QQ is not QQ");

        MessengerAlerts alerts;
        require(alerts.Flash(Messenger::WeCom, 1000), "a first flash begins a burst");
        require(!alerts.Flash(Messenger::WeCom, 1500), "flashes within a burst add nothing");
        require(alerts.Due(3000).empty(), "nothing is due before the wait");
        auto due = alerts.Due(4000);
        require(due.size() == 1 && due[0] == Messenger::WeCom, "a burst with no card gets a plain one");
        require(alerts.Due(9000).empty(), "once");
        require(!alerts.Flash(Messenger::WeCom, 9000), "a flash soon after the last is the same burst");
        require(alerts.Flash(Messenger::WeCom, 30000), "after a quiet spell a new burst");
        alerts.Noticed(Messenger::WeCom, 31000);
        require(alerts.Due(34000).empty(), "a card with the text follows: no plain card");
        alerts.Noticed(Messenger::QQ, 50000);
        require(alerts.Flash(Messenger::QQ, 52000), "a QQ burst");
        require(alerts.Due(56000).empty(), "a card just before the flash counts too");
        bool waiting = false;
        require(alerts.Flash(Messenger::WeChat, 60000) && alerts.Due(61000, &waiting).empty() && waiting, "still waiting");
        std::cout << "messengers: all checks passed\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "messengers: " << e.what() << "\n";
        return 1;
    }
}
