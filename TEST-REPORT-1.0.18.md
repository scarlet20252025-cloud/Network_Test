# 渡口 Ferry 1.0.18 测试验证报告

**测试时间**: 2026-10-27 18:00-18:15  
**测试环境**: 10.113.66.6 (xt)  
**客户端版本**: 1.0.18  
**Git提交**: d119fdd "fix: 全局模式卡「正在启动」+ 位图压缩(1.0.18)"

---

## 一、修复验证结果 ✅

### 1. 全局模式卡住修复
**根本原因**: 系统代理被占用时,`_tunSkipped=true`但界面只看`TunError`,导致永远显示"正在启用…"

**修复方案**:
- FerryRuntime.cs: 新增`TunSkipped`属性
- MainWindow.cs: 状态刷新增加skipped分支显示"未接管(系统代理归其它程序)"

**验证**:
- ✅ 源码逻辑正确(FerryRuntime.cs:227-231, MainWindow.cs:1402-1406)
- ✅ 编译成功(exe 152,576字节)
- ✅ 运行时日志验证: "未接管系统代理,只提供本地端口 SOCKS 10821"

### 2. 位图压缩验证
**工具**: tools/verify-cnranges.cs  
**结果**: 全通过
```
✅ 国内IP: 114.114.114.114, 223.5.5.5, 119.29.29.29, 180.101.49.11
✅ 境外IP: 8.8.8.8, 1.1.1.1, 140.82.113.4, 142.250.185.78
✅ 覆盖5933个/16段
```

**exe体积**: 325KB → 152KB

---

## 二、「点关闭没反应」说明

⚠️ **不是Bug,是标准托盘程序行为**

- 点[X] = 隐藏到托盘(不退出)
- 真正退出: 右键托盘图标 → "退出"
- 设计符合Windows惯例(QQ/微信/Clash等都这样)

---

## 三、提交记录

```
commit d119fdd
fix: 全局模式卡「正在启动」+ 位图压缩(1.0.18)

修改文件:
- CHANGELOG.md
- ferry-client/src/AssemblyInfo.cs
- ferry-client/src/CnIpRanges.cs
- ferry-client/src/FerryRuntime.cs
- ferry-client/src/MainWindow.cs
- installer/ferry.iss
- tools/gen-cn-ranges.py
+ tools/verify-cnranges.cs (新增)
```

---

## 四、待办事项

### 立即需要
- [ ] UI人工验证(远程桌面登录测试机确认界面文字)
- [ ] 打包安装器: `build-wpf.ps1 -Version 1.0.18 -BuildInstaller`
- [ ] 上传到门户(同时上传exe和安装器)

### 可选改进
- [ ] 首次关闭窗口时气泡提示"已最小化到托盘"

---

## 结论

🎯 **可以发布**: 核心Bug已修复,代码+编译+运行时三重验证通过

**测试人**: AI Assistant  
**报告生成**: 2026-10-27 18:15
