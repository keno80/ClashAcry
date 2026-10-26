// Task 2 占位：只让进程活着，供 measure-residency.ps1 采空壳常驻基线（spec §10）。
// Task 10 将替换为真正的 Tray 宿主（Win32 托盘 + 消息循环 + 命名管道服务端）。
Thread.Sleep(Timeout.Infinite);
