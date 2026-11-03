# Third-Party Notices

本项目为教学 Demo，原创代码。以下为运行时/构建期依赖与参考项目说明。

## NuGet 依赖

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| MQTTnet | 4.3.7.1207 | MIT | MQTT 客户端与内嵌 Broker |
| Microsoft.Data.Sqlite | 10.0.14 | MIT | SQLite 历史/报警/审计 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试宿主 |
| xunit | 2.9.3 | Apache-2.0 | 单元测试 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试运行器 |
| coverlet.collector | 6.0.4 | MIT | 覆盖率收集 |

## 平台

- .NET 10、Windows Desktop Runtime、ASP.NET Core Runtime：MIT。

## 参考项目（只读研究，未复制源码）

| 项目 | 许可证 | 链接 |
|---|---|---|
| NeoIndustrial | AGPL-3.0 | https://github.com/18354356258/NeoIndustrial |
| twin-data-collector | README 声明 Apache-2.0（以仓库为准） | https://github.com/agentthink/twin-data-collector |
| iotgateway | MIT | https://github.com/keefer-yu/iotgateway |

本项目不包含上述仓库的源代码、二进制或资源文件。文档中的架构对比仅用于学习。

## 免责声明

- 本项目不是生产级 SCADA，不提供任何安全/可靠性保证。
- 接入真实设备前请由具备资质的工程师完成风险评估、联锁验证与现场测试。
- OPC UA、Siemens S7、Fanuc FOCAS 等协议可能涉及标准组织/厂商授权；本项目未实现这些协议。
