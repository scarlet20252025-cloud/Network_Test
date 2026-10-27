using System;
using System.Net;
using System.Net.Sockets;

namespace Ferry
{
    /// <summary>
    /// 国内 IPv4 段表（数据来源：APNIC delegated-apnic-latest 的 CN 段）。
    ///
    /// 为什么需要它：客户端要判断「这个目标在国内还是境外」，才能决定走渡口
    /// 还是交给用户本机的代理。之前的实现是「猜几个大厂 IP 段」，非常不可靠
    /// —— 比如 google.com 在国内 DNS 上被污染成 31.13.92.37（Facebook 的段）。
    ///
    /// 为什么用「段表 + 二分」而不是 GeoIP 库：GeoIP 要带几十 MB 数据文件，
    /// 而我们要的判断只有一个布尔值，用分配数据就够了。表以 base64 形式编译进
    /// exe（约 44 KB 文本），首次使用时解析一次。
    ///
    /// 更新方式：`python tools/gen-cn-ranges.py`（需要同目录放一份
    /// delegated-apnic-latest，见脚本头注释）。
    /// </summary>
    internal static class CnIpRanges
    {
        static readonly uint[] _ranges = Build();

        const string Raw =
            "AQABAAEAA/8BAAgAAQAP/wEAIAABAD//AQEAAAEBAP8BAQIAAQE//wECAAABAgL/AQIEAAECf/8BAwAAAQP//wEEAQABBH//AQgA" +
            "AAEI//8BCgAAAQoJ/wEKCwABCn//AQwAAAEP//8BGAAAAR///wEtAAABLf//ATAAAAEz//8BOAAAAT///wFEAAABR///AVAAAAFf" +
            "//8BdAAAAXf//wG0AAABuf//AbwAAAHH//8BygAAAc///w4AAAAOAAf/DgAMAA4AD/8OAQAADgED/w4BGAAOARv/DgFgAA4BY/8O" +
            "AWwADgFv/w4QAAAOH///DmaAAA5mg/8OZpwADmaf/w5mtAAOZrf/DmcAAA5///8OggAADoP//w6GAAAOh///DpAAAA6f//8OwDwA" +
            "DsA//w7ATAAOwE//DsQAAA7F//8OzAAADs3//w7QAAAO3///GwCAABsAh/8bAKAAGwCn/xsAvAAbAL//GwDMABsA1/8bCAAAGx//" +
            "/xsi6AAbIu//GyQAABsv//8bMigAGzIv/xsygAAbMv//GzZIABs2T/8bNpgAGzaf/xs2wAAbNv//G2LQABti//8bY4AAG2P//xtn" +
            "AAAbZ///G2qAABtqv/8baswAG2rP/xttIAAbbT//G218ABttf/8bcAAAG3A//xtwUAAbcF//G3BwABtwd/8bcYAAG3G//xtzAAAb" +
            "c3//G3QsABt0L/8beUgAG3lP/xt5eAAbeX//G4AAABuB//8bg9wAG4Pf/xuQAAAbkP//G5QAABuf//8buAAAG+P//yQAAAAkAAP/" +
            "JAAIACQB//8kBAAAJAf//yQQAAAkJR//JCUkACQlJf8kJScAJCU//yQoAAAkMf//JDLiACQy4/8kMv4AJDP//yQ4AAAkP///JGAA" +
            "ACTf//8k+AAAJPv//yT+AAAk/v//JP90ACT/d/8k/4AAJP+D/yT/pAAk/6f/JP+sACT/s/8nAAAAJwAA/ycAAgAnAP//J0AAACds" +
            "//8ngAAAJ7///yhIAAAoSf//KH2AACh9//8ofkAAKH5//yiiAAAoov//KgAAACoAA/8qAAgAKgAb/yoAIAAqAD//KgCAACoBO/8q" +
            "AYAAKgH//yoEAAAqB///KjAAACo7//8qPgAAKj6v/yo+tAAqPr//Kj8AACo///8qUAAAKlH//ypTQAAqU1P/KlNYACpf//8qYEAA" +
            "KmBn/ypgbAAqYf//KmMAACpjc/8qY3gAKmN//ypkAAAqZ///KngAACp7H/8qeyQAKnv//yqAAAAqj///KpwAACqcH/8qnCQAKrt7" +
            "/yq7gAAqu///KsAAACrH//8qyQAAKsl//yrKAAAq8P//KvIAACr///8riAAAK5H//yuUAAArl///K7AAACvF//8r4AwAK+AP/yvg" +
            "GAAr4Bv/K+AsACvgL/8r4DQAK+A7/yvgRAAr4Ev/K+BQACvgU/8r4GQAK+Bn/yvgkAAr4JP/K+CgACvgo/8r4LAAK+Cz/yvguAAr" +
            "4Lv/K+DIACvg2/8r4PAAK+Dz/yvhTAAr4U//K+FUACvhV/8r4XgAK+F7/yvhrAAr4a//K+G0ACvht/8r4dAAK+HT/yvh2AAr4ff/" +
            "K+H8ACvh//8r4iAAK+J7/yvigAAr4tf/K+LsACvjC/8r4yAAK+Nr/yvjiAAr45P/K+OYACvjt/8r47wAK+Pf/yvj6AAr4+v/K+P4" +
            "ACvkR/8r5EwAK+RP/yvkZAAr5Gf/K+R0ACvke/8r5IQAK+SL/yvklAAr5Jv/K+S8ACvkv/8r5MwAK+TP/yvk8AAr5PP/K+UoACvl" +
            "K/8r5TAAK+Uz/yvlOAAr5Tv/K+VgACvlY/8r5YgAK+WT/yvlqAAr5cf/K+XYACvl3/8r5egAK+Xv/yvmFAAr5hf/K+YgACvmI/8r" +
            "5kQAK+ZL/yvmfAAr5n//K+aIACvmi/8r5twAK+b//yvnIAAr5y//K+dQACvnb/8r54gAK+e3/yvsAAAr7cP/K+3IACvvM/8r73QA" +
            "K+97/yvvrAAr77P/K/AAACvwA/8r8DgAK/A//yvwRAAr8E//K/BUACvwV/8r8HwAK/CL/yvwkAAr8JP/K/CcACvw3/8r8PAAK/EX" +
            "/yvxMAAr8TP/K/FMACvxX/8r8WQAK/Fl/yvxcAAr8XP/K/GoACvxu/8r8dAAK/Hz/yvx+AAr8f//K/IIACvyH/8r8jAAK/JD/yvy" +
            "SAAr8mP/K/KQACvyq/8r8rQAK/K3/yvyvAAr8sf/K/LMACvyz/8r8tgAK/Lf/yvy/AAr8v//K/MEACvzE/8r81gAK/Nb/yvzgAAr" +
            "84P/K/OIACvzi/8r85AAK/OX/yvznAAr85//K/O0ACvzt/8r8+QAK/Pr/yvz9AAr8/f/K/YAACv2Y/8r9nAAK/Zz/yv25AAr9uf/" +
            "K/cEACv3C/8r9ywAK/cz/yv3RAAr90f/K/dMACv3T/8r91QAK/dn/yv3bAAr93P/K/eUACv3m/8r97AAK/e//yv3xAAr+Af/K/gU" +
            "ACv4F/8r+BwAK/gf/yv4MAAr+DP/K/hMACv4l/8r+LAAK/jT/yv45AAr+Ov/K/j0ACv49/8r+QQAK/kH/yv5eAAr+Xv/K/mEACv5" +
            "i/8r+ZAAK/mr/yv5wAAr+cP/K/nsACv57/8r+gQAK/oH/yv6DAAr+hf/K/ocACv6J/8r+kgAK/pL/yv6YAAr+nf/K/qAACv6g/8r" +
            "+pAAK/qX/yv6oAAr+qP/K/qoACv6tf8r+sgAK/rL/yv61AAr+t//K/rsACv67/8r+vQAK/r3/yv7BAAr+wv/K/skACv7J/8r+3QA" +
            "K/t3/yv7wAAr+8P/K/voACv77/8r+/QAK/v3/yv8MAAr/DP/K/w4ACv8O/8r/gAAK/4L/yv+GAAr/hv/K/4kACv+J/8r/iwAK/4v" +
            "/yv+NAAr/jf/K/5AACv+Q/8r/kgAK/5L/yv+VAAr/l//K/5kACv+a/8r/nAAK/53/yv+gAAr/oP/K/6IACv+n/8r/qgAK/6v/yv+" +
            "tAAr/sv/K/7QACv+0/8r/twAK/7z/yv++AAr/wv/K/8QACv/E/8r/zAAK/8z/yv/QAAr/0//K/9UACv/V/8r/2AAK/9j/yv/kAAr" +
            "/5P/K/+wACv/s/8r/7gAK/+7/yv/wAAr/8P/K//IACv/1/8r/+AAK//r/yv/9AAr//f/LSjAAC0o//8tQRAALUEf/y1whAAtcIf/" +
            "LXC8AC1wv/8tcNAALXDf/y1w5AAtcO//LXEMAC1xH/8tcSgALXEr/y1xNAAtcTv/LXFIAC1xS/8tcZAALXGX/y1xqAAtcav/LXGw" +
            "AC1xs/8tcbgALXG7/y1xyAAtcd//LXHwAC1x8/8tcfwALXID/y1yIAAtciP/LXIoAC1yK/8tcjQALXI3/y1yYAAtcmP/LXJ8AC1y" +
            "f/8tcogALXKL/y1yxAAtcsv/LXLkAC1y5/8tcvwALXL//y1zEgAtcxP/LXMsAC1zL/8tc2QALXNn/y1zeAAtc3v/LXOEAC1zh/8t" +
            "c5AALXOT/y1znAAtc5//LXOkAC1zp/8tc8gALXPL/y1z1AAtc9f/LXPkAC1z5/8tc+wALXPv/y1z9AAtc/v/LXQQAC10E/8tdBgA" +
            "LXQb/y10IAAtdCf/LXQ0AC10N/8tdGAALXRn/y10jAAtdI//LXSYAC10m/8tdNAALXTT/y11CAAtdQv/LXUUAC11F/8tdUQALXVH" +
            "/y11fAAtdX//LXX8AC11//8tdyoALXcr/y13NAAtdzf/LXc8AC13S/8td2gALXdr/y13dAAtd3f/LXfoAC136/8teGQALXhn/y14" +
            "jAAteI//LXikAC14p/8tePAALXjz/y15NAAteTf/LXlAAC15S/8teVwALXlj/y15rAAtebP/LXnUAC151/8tefAALXor/y16PAAt" +
            "enf/LXqgAC162/8texwALXsn/y17LAAte1v/LXt4AC17e/8te4AALXuL/y17lAAte5//LXukAC17u/8te8wALXvP/y171AAte9f/" +
            "LXvgAC18A/8tfBQALXwX/y18HAAtfCf/LXwsAC18L/8tfEQALXxH/y18TAAtfFP/LXxkAC18Z/8tfHwALXx//y18rAAtfLP/LXzQ" +
            "AC180/8tfPgALXz7/y19EAAtfRP/LX0YAC19G/8tfSwALX0v/y19NAAtfTv/LX1MAC19Z/8tfYgALX2L/y1+MAAtfjf/LX5kAC1+" +
            "Z/8tfmwALX57/y1+1AAtftf/LX7cAC1+3/8tfwgALX8P/y1/gAAtf4P/LX+QAC1/l/8tf5wALX+f/y1/2AAtf9v/LfgIAC34C/8t" +
            "+FAALfhb/y34YAAt+G//LfiAAC34h/8t+MwALfkH/y35DAAt+Sf/LflwAC35c/8t+bwALfnX/y36DAAt+hP/LfocAC36K/8t+kwA" +
            "Lfpj/y36aAAt+pv/LfqkAC36p/8t+rQALfrD/y37AAAt+wP/LfsIAC37C/8t+xAALfsX/y37NAAt+zf/LftUAC37Z/8t+3gALft/" +
            "/y37iAAt++P/LfvwAC378/8t+/gALfv5/y38AAAt/DP/LfxUAC38s/8t/MAALfzr/y39AAAt/Vf/Lf1cAC39e/8t/YQALf3z/y39" +
            "9gAt/ff/Lf4AAC3+H/8t/igALf4r/y3+MAAt/uf/Lf7sAC3+8/8t/vgALf77/y3/AAAt/3v/Lf+IAC3/+/8vXAAAL3///zEEAAAx" +
            "B///MTMAADE3//8xQAAAMV///zFwAAAxe///MYAAADGAAP8xgAIAMYAH/zGMAAAxjf//MZgAADGb//8x0AAAMdP//zHVPAAx1T//" +
            "MdwAADHf//8x6AAAMev//zHvAAAx7z//Me/AADHv//8x9uAAMfb//zRQAAA0U///NIIAADSD//823gAANt///zmwAAA5sf//Og4A" +
            "ADoZ//86HgAAOj///zpB6AA6Qe//OkIAADpD//86RIAAOkT//zpSAAA6Un//OlMAADpT//86V0AAOld//zpjgAA6Zf//OnQAADp3" +
            "//86gAAAOof//zqQAAA6kP//OpoAADqb//86wAAAOt///zrwAAA6////OyAAADtT//87awAAO2///zuXAAA7l3//O5gQADuYJ/87" +
            "mEAAO5hP/zuYcAA7mHf/O5kEADuZB/87mSAAO5kj/zuZPAA7mUv/O5lcADuZX/87mXQAO5l3/zuZiAA7mYv/O5mYADuZm/87maQA" +
            "O5nD/zubAAA7m///O6wAADuv//87vwAAO79//zvAAAA8H///PDcAADw3//88PwAAPD///zygAAA8v///PMIAADzD//88yAAAPN//" +
            "/zzoAAA86f//POsAADzr//889YAAPPX//zz3AAA89///PPwAADz8//88/YAAPP3//zz/AAA8////PQRQAD0EX/89BLAAPQS//z0I" +
            "oAA9CK//PQ7UAD0O3/89DvAAPQ73/z0cAAA9HH//PR2AAD0d8/89HfgAPR37/z0tgAA9Lb//PS3gAD0t7/89L4AAPS+//z0wAAA9" +
            "N///PVfAAD1X//89gAAAPb///z3oAAA97f//PfAAAD3z//8+6gAAPur//0RPAABETz//RebAAEXm//9F54AARee//0XqwABF6v//" +
            "ReuAAEXrv/9Hg8AAR4Q//0eIQABHiH//R4kAAEeJP/9RRAAAUUf//1KcAABSnf//Xr8AAF6/f/9lAAAAZQAD/2UBAABlAQP/ZQKs" +
            "AGUCr/9lBAAAZQf//2UQAABlH///ZSGAAGUkX/9lJIAAZSv//2UwAABlMf//ZTIIAGUyD/9lMjgAZTI7/2U0AABlNP//ZTVkAGU1" +
            "Z/9lNgAAZTb//2U34ABlN+f/ZUAAAGVOA/9lTiAAZU4//2VQAABlYAv/ZWAQAGVgH/9lYIAAZWD//2VjYABlY3//ZWVAAGVlX/9l" +
            "ZWQAZWVk/2VlZgBlZX//ZWZAAGVmX/9lZmQAZWZm/2VmaABlZn//ZWgAAGVr//9lbkAAZW5v/2VudABlbn//ZXgAAGV+//9lgAAA" +
            "ZYAD/2WACABlgD//ZYEAAGWH//9lkAAAZZ///2XAAABlyf//ZcuAAGXLp/9ly6wAZcu//2XMAABlz///ZeAAAGXp//9l6kAAZepH" +
            "/2XqTABl6n//ZewAAGX5//9l+wAAZfsD/2X7CABl/v//ZwEIAGcBC/9nARQAZwEb/2cBSABnAUv/ZwFYAGcBW/9nAagAZwGr/2cC" +
            "bABnAm//ZwKcAGcCn/9nAqQAZwKn/2cCvABnAr3/ZwLIAGcC1/9nA1QAZwOP/2cDlABnA5//ZwQ4AGcEO/9nBKgAZwSr/2cEuABn" +
            "BLv/ZwTgAGcE4/9nBSQAZwUn/2cFNABnBTv/ZwWYAGcFm/9nBagAZwWr/2cFwABnBcP/ZwX8AGcF//9nBkwAZwZP/2cGbABnBm//" +
            "ZwbcAGcG3/9nBuQAZwbn/2cHHABnBx//ZweMAGcHj/9nB9QAZwff/2cIAABnCAv/ZwggAGcII/9nCDQAZwg3/2cIRABnCEf/Zwhs" +
            "AGcIb/9nCJwAZwif/2cIyABnCM//ZwjcAGcI3/9nCQgAZwkL/2cJGABnCRv/ZwlsAGcJb/9nCZgAZwmb/2cJ+ABnCgP/ZwoQAGcK" +
            "E/9nClQAZwpX/2cKjABnCo//ZwsQAGcLE/9nC6gAZwur/2cLtABnC7f/ZwwgAGcMI/9nDFwAZwxf/2cMYgBnDGP/ZwyIAGcMi/9n" +
            "DLgAZwy7/2cM6ABnDOv/Zw0MAGcND/9nDXwAZw1//2cNxABnDcf/Zw30AGcN9/9nDlQAZw5X/2cOhABnDov/Zw6cAGcOn/9nDvAA" +
            "Zw7z/2cPBABnDwv/Zw8QAGcPE/9nD2AAZw9j/2cPyABnD8v/ZxA0AGcQN/9nEFAAZxBb/2cQbABnEG//ZxB8AGcQf/9nESgAZxEr" +
            "/2cRQABnEUP/ZxF4AGcRe/9nEYgAZxGL/2cRoABnEaP/ZxHMAGcRz/9nEeQAZxHn/2cSugBnErv/ZxLAAGcSw/9nEs4AZxLX/2cS" +
            "4ABnEuP/ZxMMAGcTD/9nEygAZxMv/2cTMgBnEzP/ZxNAAGcTS/9nE+gAZxPr/2cUDABnFA//ZxQgAGcUI/9nFCwAZxQv/2cURABn" +
            "FEf/ZxRwAGcUc/9nFIAAZxSD/2cUoABnFKP/ZxT4AGcU+/9nFWIAZxVj/2cVZgBnFWf/ZxVwAGcVd/9nFYgAZxWP/2cVsABnFbP/" +
            "ZxXQAGcV0/9nFfAAZxXz/2cWAABnFl//ZxZkAGcWf/9nFrwAZxa//2cW5ABnFuf/Zxb8AGcW//9nFwgAZxcL/2cXOABnFzv/Zxeg" +
            "AGcXp/9nF7AAZxez/2cX5ABnF+f/ZxgYAGcYG/9nGHQAZxh3/2cYgABnGIP/ZxiQAGcYk/9nGLAAZxiz/2cYuABnGLv/ZxjcAGcY" +
            "3/9nGOQAZxjn/2cY/ABnGP//ZxkIAGcZCf9nGRQAZxkr/2cZMABnGTP/ZxlAAGcZR/9nGZQAZxmX/2cZnABnGZ//ZxnYAGcZ2/9n" +
            "GgAAZxoD/2caQABnGkP/ZxpMAGcaT/9nGoQAZxqH/2canABnGqP/ZxrkAGca5/9nGvAAZxrz/2cbBABnGwf/ZxsMAGcbD/9nGxgA" +
            "Zxsb/2cbOABnGzv/ZxtgAGcbY/9nG7gAZxu7/2cb1ABnG9f/ZxvwAGcb8/9nHAQAZxwL/2ccuABnHLv/ZxzMAGccz/9nHNQAZxzX" +
            "/2cdEABnHRP/Zx0YAGcdGf9nHR0AZx0d/2cdgABnHYv/Zx3sAGcd7f9nHhQAZx4X/2ceYABnHmP/Zx5oAGcea/9nHpQAZx6X/2ce" +
            "yABnHsv/Zx7kAGce5/9nHuwAZx7v/2cfAABnHwP/Zx8wAGcfR/9nH5QAZx+X/2cfoABnH6P/Zx+oAGcfq/9nH8gAZx/L/2cf7ABn" +
            "H+//Zx/yAGcf8/9nIAAAZyMz/2cjaABnI2v/ZyN0AGcjd/9nI7QAZyO3/2cj3ABnI9//ZyQcAGckH/9nJCQAZyQn/2ckOABnJEP/" +
            "ZyRIAGckS/9nJGAAZyRj/2ckhABnJIv/ZySgAGck9/9nJQwAZyUT/2clGABnJRv/ZyUsAGclL/9nJTQAZyU7/2clSABnJUv/ZyVk" +
            "AGcla/9nJYgAZyWn/2clrABnJbP/ZyW8AGclv/9nJdAAZyXf/2cl/ABnJgP/ZyYgAGcmI/9nJigAZyYv/2cmOABnJjv/ZyZMAGcm" +
            "T/9nJlQAZyZX/2cmXABnJmP/ZyZ0AGcmd/9nJoQAZyaH/2cmjABnJo//ZybgAGcm6/9nJ0AAZydD/2cnWABnJ1v/ZydkAGcna/9n" +
            "J6AAZye//2cnyABnJ+v/ZygMAGcoL/9nKFgAZyhb/2congBnKJ//ZyiuAGcor/9nKMAAZyjD/2co1ABnKNf/ZyjcAGco3/9nKOQA" +
            "ZykD/2cpEABnKRP/Zyk0AGcpN/9nKXQAZyl3/2cpjABnKY//ZymUAGcpm/9nKaAAZymn/2cp3ABnKev/ZyoIAGcqC/9nKhgAZyoj" +
            "/2cqQABnKkf/ZypMAGcqT/9nKmgAZypr/2cqtABnKrf/ZyroAGcq6/9nKxAAZysT/2crYABnK2v/Zyt8AGcrf/9nK4QAZyuH/2cr" +
            "uABnK7v/ZyvAAGcrx/9nK9AAZyvT/2cr3ABnK+P/ZyvwAGcr8/9nLDgAZyw7/2csUABnLFP/Zyx4AGcsf/9nLIQAZyyH/2cskABn" +
            "LJP/ZyyoAGcsq/9nLLAAZyzP/2cs4ABnLOP/ZyzsAGctP/9nLUgAZy3j/2ct+ABnLfv/Zy4AAGcuA/9nLgwAZy6L/2cumABnLrf/" +
            "Zy70AGcu+/9nLwQAZy8H/2cvFABnLxf/Zy8kAGcvK/9nLzAAZy8z/2cvUABnL1P/Zy9gAGcvY/9nL2wAZy9v/2cvdABnL3v/Zy+I" +
            "AGcvj/9nL9QAZy/X/2cwNABnMDf/ZzBcAGcwX/9nMJQAZzCf/2cwygBnMMv/ZzDYAGcw9/9nMQwAZzEP/2cxFABnMRf/ZzFIAGcx" +
            "T/9nMWAAZzFj/2cxbABnMW//ZzGwAGcxt/9nMcQAZzHH/2cyJABnMif/ZzIsAGcyS/9nMmwAZzJ//2cyhABnMo//ZzKsAGcyy/9n" +
            "MtwAZzL7/2c0KABnNCv/ZzRIAGc0V/9nNGAAZzRr/2c0oABnNKf/ZzSsAGc0s/9nNLgAZzS7/2c0xABnNMf/ZzVAAGc1R/9nNVwA" +
            "ZzVf/2c1ZABnNWf/ZzV8AGc1k/9nNbQAZzW3/2c1zABnNdf/ZzXsAGc17/9nNfgAZzX7/2c2CABnNgv/ZzYwAGc2M/9nNqAAZzan" +
            "/2c21ABnNtf/ZzbwAGc28/9nN1AAZzdT/2c3eABnN3v/ZzeYAGc3m/9nN6wAZzev/2c3zABnN9P/ZzfkAGc35/9nN+wAZzfv/2c4" +
            "CABnOAv/ZzgQAGc4F/9nOCAAZzgj/2c4OABnOD//ZzhIAGc4T/9nOF4AZzhf/2c4ZABnOGv/ZziMAGc4j/9nOJgAZzib/2c4uABn" +
            "OLv/ZzjIAGc4y/9nOQwAZzkP/2c5NABnOTv/ZzlMAGc5T/9nOYgAZzmL/2c5xABnOcf/ZzoYAGc6G/9nO0wAZztP/2c7ZABnO2f/" +
            "ZztwAGc7g/9nO5QAZzuX/2c7pABnO6n/ZzwgAGc8I/9nPCwAZzwv/2c8pABnPKf/ZzzkAGc85/9nPOwAZzzv/2c9PABnPT//Zz1o" +
            "AGc9a/9nPYwAZz2P/2c9mABnPaP/Zz2sAGc9s/9nPbwAZz2//2c+GABnPhv/Zz40AGc+N/9nPkgAZz5b/2c+YABnPof/Zz6cAGc+" +
            "w/9nPswAZz7j/2c/IABnP1v/Zz+MAGc/k/9nP5gAZz+b/2c/oABnP7v/Zz/AAGc/0/9nP/AAZ0AH/2dAGABnQH//Z0CMAGdAk/9n" +
            "QJgAZ0ET/2dBMABnQV//Z0FkAGdBc/9nQZAAZ0Gv/2dBzABnQc//Z0HgAGdB4f9nQiAAZ0Ij/2dCKABnQiv/Z0JcAGdCX/9nQmwA" +
            "Z0Jv/2dCyABnQsv/Z0LwAGdDC/9nQzQAZ0M1/2dDZABnQ5f/Z0OsAGdDr/9nQ8AAZ0PD/2dD1ABnQ9f/Z0P8AGdD//9nREAAZ0RD" +
            "/2dEWABnRFv/Z0RkAGdEZ/9nRIAAZ0SD/2dEwABnRMP/Z0UQAGdFE/9nRT4AZ0U//2dFdABnRXf/Z0YIAGdGC/9nRg4AZ0YP/2dG" +
            "lABnRpf/Z0bcAGdG4/9nRuwAZ0bv/2dG/ABnRwP/Z0cwAGdHM/9nR0QAZ0dL/2dHUABnR1v/Z0d4AGdHg/9nR5AAZ0eT/2dHxABn" +
            "R8v/Z0foAGdH6/9nSAwAZ0g3/2dIcABnSIf/Z0iUAGdIl/9nSKwAZ0iv/2dItABnSLf/Z0jgAGdJH/9nSTAAZ0kz/2dJdABnSXv/" +
            "Z0mAAGdJk/9nSagAZ0mr/2dJsABnSbP/Z0nMAGdJ0/9nSfAAZ0n7/2dKGABnSjP/Z0o4AGdKP/9nSlAAZ0pT/2dKfABnSn//Z0qU" +
            "AGdKn/9nSswAZ0rP/2dK6ABnSuv/Z0tSAGdLU/9nS1gAZ0tf/2dLaABnS3P/Z0t4AGdLe/9nS4AAZ0uD/2dLkABnS5P/Z0uYAGdL" +
            "m/9nS+wAZ0vs/2dMPABnTEv/Z0xcAGdMX/9nTNgAZ0zj/2dNHABnTR//Z000AGdNO/9nTUgAZ01L/2dNWABnTV//Z02EAGdNh/9n" +
            "TZQAZ02X/2dN3ABnTd//Z044AGdOR/9nTnwAZ05//2dOrABnTrP/Z07EAGdOx/9nTuQAZ07n/2dPGABnTx//Z08kAGdPL/9nTzgA" +
            "Z09H/2dPUABnT1f/Z0+IAGdPi/9nT7wAZ0/X/2dP5ABnT+X/Z1AcAGdQH/9nUCwAZ1Av/2dQSABnUEv/Z1CwAGdQu/9nUMAAZ1DD" +
            "/2dQyABnUMv/Z1DoAGdQ6/9nUQQAZ1EL/2dREABnURf/Z1EsAGdRM/9nUWAAZ1Fj/2dReABnUXv/Z1GUAGdRl/9nUaQAZ1Gr/2dR" +
            "twBnUbv/Z1HIAGdRy/9nUegAZ1Hr/2dSPABnUj//Z1JEAGdSR/9nUlQAZ1JX/2dSaABnUmv/Z1LgAGdS4/9nUuwAZ1Lv/2dTLABn" +
            "Uy//Z1M0AGdTN/9nUzwAZ1ND/2dTSABnU0v/Z1NwAGdTc/9nU3gAZ1N7/2dTtABnU7f/Z1QAAGdUA/9nVAwAZ1Qf/2dUMABnVDP/" +
            "Z1RAAGdUQ/9nVEgAZ1RL/2dUiABnVIv/Z1SqAGdUq/9nVRQAZ1Ub/2dVLABnVTP/Z1VUAGdVV/9nVYgAZ1WL/2dVkABnVZP/Z1Wk" +
            "AGdVs/9nVboAZ1W7/2dV4ABnVeP/Z1YcAGdWI/9nVjwAZ1Y//2dWVABnVlf/Z1bMAGdXB/9nVxQAZ1cX/2dXIABnVyP/Z1dgAGdX" +
            "Y/9nV4QAZ1eH/2dXtABnV7f/Z1fgAGdX4/9nWAQAZ1gX/2dYIABnWCf/Z1g8AGdYQ/9nWEgAZ1hL/2dYYABnWGP/Z1ikAGdYp/9n" +
            "WNQAZ1jX/2dZHABnWR//Z1lgAGdZd/9nWZQAZ1mX/2dZrABnWa//Z1m4AGdZ5/9nWjMAZ1o5/2daXABnWl//Z1pkAGdah/9nWpgA" +
            "Z1qb/2daqABnWqv/Z1qwAGdas/9nWrwAZ1rD/2dbJABnWyv/Z1tsAGdbcf9nW4oAZ1uL/2dbmABnW5v/Z1uwAGdbs/9nW8gAZ1vL" +
            "/2db0ABnW9f/Z1vsAGdb7/9nW/wAZ1wP/2dcMABnXFP/Z1xWAGdcVv9nXFgAZ1xb/2dcbABnXG//Z1x8AGdcf/9nXIQAZ1yH/2dc" +
            "nABnXJ//Z1ykAGdcw/9nXOwAZ10H/2ddHABnXR//Z11UAGddV/9nXY4AZ12P/2ddmABnXZv/Z120AGddt/9nXcwAZ13P/2deDABn" +
            "Xg//Z14UAGdeF/9nXhwAZ14v/2deSABnXkv/Z15YAGdeW/9nXnQAZ153/2deoABnXqP/Z17IAGdey/9nXzQAZ183/2dfRABnX0f/" +
            "Z19YAGdfX/9nX4AAZ1+D/2dfiABnX5P/Z1+YAGdfm/9nX9gAZ1/j/2df7ABnX///Z2AIAGdgC/9nYHwAZ2B//2dgiABnYIz/Z2CY" +
            "AGdg2/9nYOAAZ2Dh/2dhCABnYSv/Z2E4AGdhS/9nYXAAZ2F3/2dhgABnYYP/Z2GQAGdhl/9nYbwAZ2HD/2dh5ABnYeX/Z2IAAGdi" +
            "Af9nYigAZ2Iz/2diOABnYjv/Z2JQAGdiU/9nYlgAZ2Jn/2difABnYn//Z2KIAGdik/9nYqQAZ2Kr/2ditABnYrf/Z2LEAGdix/9n" +
            "YtgAZ2Lr/2di8ABnYv//Z2MoAGdjKf9nYzQAZ2M//2djTABnY0//Z2NoAGdja/9nY3QAZ2N7/2djmABnY5v/Z2PcAGdj3/9nY+gA" +
            "Z2Pv/2dkAABnZAP/Z2QgAGdkI/9nZCgAZ2Qr/2dkMABnZEf/Z2RYAGdkW/9nZHQAZ2R3/2dkkABnZJP/Z2TsAGdk8/9nZPgAZ2T/" +
            "/2dlCABnZQ//Z2U8AGdlP/9nZXgAZ2V//2dlkABnZZf/Z2W0AGdlu/9nZkwAZ2ZT/2dmqABnZq//Z2a0AGdmy/9nZtAAZ2bX/2dn" +
            "DABnZxP/Z2ckAGdnJ/9nZ0gAZ2dL/2dnvABnZ7//Z2fIAGdnz/9nZ9wAZ2fr/2dn+ABnaAf/Z2gkAGdoK/9naEAAZ2hD/2doaABn" +
            "aGv/Z2iYAGdom/9naLwAZ2i//2doxgBnaMf/Z2j8AGdpB/9naQwAZ2kT/2dpFwBnaRf/Z2k8AGdpP/9naXQAZ2l3/2dphABnaYf/" +
            "Z2m0AGdpu/9nacgAZ2nP/2dp3ABnad//Z2okAGdqL/9najwAZ2o//2dqRABnakf/Z2pgAGdqY/9nangAZ2p7/2dqgABnaof/Z2qg" +
            "AGdqo/9nasQAZ2rH/2dqygBnasv/Z2rUAGdq1/9navwAZ2sD/2drHABnayP/Z2ssAGdrL/9na0gAZ2tL/2drbABna2//Z2ukAGdr" +
            "q/9na7wAZ2vD/2dr0ABna9//Z2w0AGdsN/9nbKAAZ2yn/2dswABnbMf/Z2zQAGds1/9nbOAAZ2zj/2ds9ABnbPf/Z20UAGdtF/9n" +
            "bTAAZ20z/2dtWABnbVv/Z21qAGdta/9nbfgAZ237/2duXABnbl//Z250AGdud/9nboQAZ26L/2dumABnbp//Z268AGduv/9nbswA" +
            "Z27P/2dvQABnb0P/Z2+sAGdvr/9nb/wAZ2///2dwRABncEv/Z3BYAGdwX/9ncGwAZ3B3/2dwjABncI//Z3CsAGdwr/9ncLgAZ3C7" +
            "/2dxBABncQf/Z3FcAGdxX/9ncZAAZ3GT/2dx3ABncd//Z3HoAGdx7/9ncgQAZ3IH/2dyHABnch//Z3JEAGdyS/9ncmQAZ3Jn/2dy" +
            "lABncpf/Z3KcAGdyn/9ncrAAZ3Kz/2dy1ABnctf/Z3LsAGdy8/9ncxAAZ3MT/2dzKABnc0f/Z3NcAGdzX/9nc3gAZ3N7/2dzlABn" +
            "c5f/Z3P4AGdz+/9ndCgAZ3Qr/2d0TABndE//Z3RcAGd0X/9ndHgAZ3R7/2d0gABndIP/Z3SKAGd0i/9ndJQAZ3SX/2d0uABndLv/" +
            "Z3TcAGd05/9ndRAAZ3UT/2d1SABndUv/Z3VYAGd1W/9ndbwAZ3W//2d13ABndd//Z3Y0AGd2S/9ndlgAZ3Zb/2d2rQBndq3/Z3bA" +
            "AGd23/9ndvAAZ3cD/2d3DABndxP/Z3ccAGd3H/9nd2gAZ3dr/2d3cwBnd3P/Z3ecAGd3n/9nd7QAZ3e3/2d3yABnd8v/Z3fgAGd3" +
            "4/9neDQAZ3g3/2d4SABneEv/Z3hYAGd4W/9neGAAZ3hn/2d4jABneI//Z3jEAGd4x/9neOAAZ3jj/2d5NABneTf/Z3mgAGd5p/9n" +
            "efoAZ3n6/2d5/ABnef//Z3owAGd6M/9nerAAZ3qz/2d6wABnesP/Z3rwAGd68/9newQAZ3sH/2d7OABnezv/Z3tYAGd7X/9ne3QA" +
            "Z3t3/2d7sABne7P/Z3vIAGd71/9nfBgAZ3wb/2d8MABnfDP/Z3xAAGd8Q/9nfNQAZ3zb/2d9FABnfRf/Z30sAGd9L/9nfYQAZ32H" +
            "/2d9pABnfaf/Z33EAGd9x/9nfewAZ33v/2d9+ABnffv/Z34AAGd+A/9nfhAAZ34T/2d+LABnfi//Z35kAGd+Z/9nfnwAZ36H/2d+" +
            "0ABnftP/Z4KEAGeCh/9ngqAAZ4Kj/2eC5ABnguf/Z4MUAGeDF/9ngyQAZ4Mn/2eDigBng4v/Z4OYAGeDm/9ng6gAZ4Or/2eDsABn" +
            "g7P/Z4PgAGeD5/9ng/AAZ4Pz/2eEFgBnhBf/Z4QwAGeEM/9nhDwAZ4RT/2eEaABnhHv/Z4SgAGeEp/9nhLwAZ4S//2eE0ABnhNf/" +
            "Z4TqAGeE6/9nhQwAZ4UP/2eFKABnhSv/Z4WAAGeFg/9nhbAAZ4Wz/2eF6ABnhev/Z4bEAGeGx/9nhugAZ4bp/2eHUABnh1P/Z4d8" +
            "AGeHf/9nh5QAZ4eX/2eHnABnh6f/Z4ewAGeHs/9nh7gAZ4e7/2eHwABnh8f/Z4fsAGeH7/9niIAAZ4iD/2eI6ABniOv/Z4k6AGeJ" +
            "PP9niUwAZ4lP/2eJiABniYn/Z4m0AGeJt/9niewAZ4nv/2eKAgBnigP/Z4oMAGeKDf9nilAAZ4pT/2eKhgBniof/Z4qcAGeKnf9n" +
            "itAAZ4rR/2eK3ABnit3/Z4r4AGeK+f9niwAAZ4sD/2eLFgBnixf/Z4tcAGeLXf9ni4YAZ4uJ/2eLrABni63/Z4vMAGeLzf9ni9QA" +
            "Z4vV/2eMCABnjAn/Z4wOAGeMD/9njH4AZ4x//2eMjABnjI3/Z4yQAGeMkf9njJgAZ4yZ/2eMwABnjMH/Z4zkAGeM5f9njQoAZ40L" +
            "/2eNOgBnjTv/Z42AAGeNgf9njboAZ427/2eN8gBnjfP/Z44cAGeOHf9njjoAZ447/2eOUgBnjlP/Z45gAGeOYf9njmYAZ45n/2eO" +
            "egBnjnv/Z46AAGeOgf9njowAZ46N/2eOmgBnjp3/Z46sAGeOrf9njrQAZ461/2eOugBnjrv/Z47cAGeO3f9njuoAZ47r/2eO7gBn" +
            "ju//Z474AGeO+f9njxAAZ48T/2ePSgBnj0v/Z494AGePef9nj3wAZ499/2ePhABnj4f/Z4+uAGePr/9nj+QAZ4/l/2eQKABnkCn/" +
            "Z5A0AGeQNf9nkEIAZ5BD/2eQRgBnkEn/Z5CIAGeQif9nkJ4AZ5Cf/2eQ8ABnkPH/Z5EmAGeRJ/9nkSoAZ5Er/2eRXABnkV//Z5Fi" +
            "AGeRY/9nkbwAZ5G//2eSSABnkkn/Z5JaAGeSW/9nknwAZ5J//2eSigBnkov/Z5LsAGeS7f9nkwwAZ5MN/2eTfABnk33/Z5PGAGeT" +
            "x/9nk84AZ5PP/2eUrgBnlK//Z5UGAGeVB/9nlREAZ5UR/2eVLABnlS3/Z5WEAGeVhf9nlZAAZ5WR/2eVtQBnlbX/Z5XSAGeV0/9n" +
            "ldYAZ5XX/2eV3ABnld3/Z5XyAGeV9/9nlgoAZ5YL/2eWGABnlhn/Z5ZCAGeWQ/9nlkgAZ5ZJ/2eWegBnlnv/Z5Z+AGeWg/9nlpIA" +
            "Z5aT/2eWpABnlqX/Z5asAGeWrf9nlrQAZ5a1/2eWyABnlsn/Z5bSAGeW0/9nltYAZ5bZ/2eW9ABnlvX/Z5cEAGeXBf9nlywAZ5ct" +
            "/2eXjgBnl4//Z5eUAGeXl/9nl54AZ5ef/2eX2ABnl9n/Z5fkAGeX5f9nmBwAZ5gf/2eYOABnmDn/Z5hMAGeYTf9nmHAAZ5hx/2eY" +
            "eABnmHv/Z5iYAGeYmf9nmKgAZ5ir/2eYugBnmLv/Z5jAAGeYwf9nmMgAZ5jJ/2eY0ABnmNH/Z5jgAGeY4f9nmPYAZ5j3/2eY+gBn" +
            "mPv/Z5kEAGeZBf9nmSQAZ5kl/2eZZABnmWX/Z5lyAGeZc/9nmXoAZ5l7/2eZhABnmYX/Z5mKAGeZi/9nmZIAZ5mT/2eZoABnmaH/" +
            "Z5oSAGeaE/9nmh4AZ5oh/2eaKABnmin/Z5pCAGeaQ/9nmqIAZ5ql/2eaqABnmqn/Z5sOAGebD/9nmyIAZ5sj/2ebMABnmzH/Z5tM" +
            "AGebTf9nm2QAZ5tl/2ebbgBnm2//Z5vWAGeb1/9nm/gAZ5v5/2ecHABnnB3/Z5xEAGecRf9nnE4AZ5xP/2ecaABnnGn/Z5yeAGec" +
            "n/9nnK4AZ5yv/2ecugBnnLv/Z5zkAGec5f9nnR4AZ50f/2edrgBnna//Z53UAGed1f9nneoAZ53r/2ed/gBnngH/Z54IAGeeCf9n" +
            "nhAAZ54R/2eeSgBnnkv/Z56+AGeev/9nnsgAZ57J/2ee4ABnnuH/Z596AGefff9nn4YAZ5+H/2efjgBnn4//Z6AgAGegI/9noHAA" +
            "Z6Bz/2eg9ABnoPX/Z6D+AGeg//9noQ4AZ6EP/2ehZgBnoWf/Z6HQAGeh0f9nodwAZ6Hd/2eh/gBnof//Z6IKAGeiC/9noiAAZ6Ih" +
            "/2eidABnonX/Z6McAGejHf9noyAAZ6Mh/2ejLgBnoy//Z6NKAGejS/9no7QAZ6O1/2ekBABnpAX/Z6QgAGekIf9npCgAZ6Qr/2ek" +
            "QABnpEH/Z6RMAGekTf9npLIAZ6Sz/2elLABnpS3/Z6U0AGelNf9npVIAZ6VT/2elbgBnpW//Z6YUAGemFf9npjIAZ6Y3/2emVABn" +
            "plX/Z6aKAGemi/9npwAAZ6cB/2enJABnpyX/Z6dkAGenZf9nqGIAZ6hj/2eoqgBnqKv/Z6kyAGepM/9nqT4AZ6k//2epbABnqW3/" +
            "Z6miAGepo/9nqcoAZ6nL/2ep2ABnqdn/Z6oEAGeqBf9nqkgAZ6pJ/2eqhgBnqof/Z6rSAGeq1f9nqyAAZ6sh/2erpgBnq6f/Z6vW" +
            "AGer1/9nrCAAZ6wh/2esoABnrKH/Z6y/AGesv/9nrWYAZ61n/2ettgBnrbn/Z65eAGeuX/9nrw4AZ68P/2evYgBnr2P/Z69yAGev" +
            "c/9nr3YAZ693/2ewNABnsDX/Z7DeAGew3/9nsPQAZ7D1/2exHABnsR3/Z7EsAGexLf9nsUYAZ7FH/2exiABnsYn/Z7GiAGexo/9n" +
            "sjgAZ7I5/2ey8ABnsvH/Z7NMAGezT/9ntGwAZ7Rt/2e04gBntOP/Z7WkAGe1pf9nteoAZ7Xr/2e3GgBntxv/Z7dCAGe3Q/9nt3oA" +
            "Z7d9/2e4LgBnuC//Z7g8AGe4Pf9nuU4AZ7lR/2e55ABnueX/Z7oEAGe6Bf9numwAZ7pt/2e6cABnunH/Z7qIAGe6if9nuqIAZ7qj" +
            "/2e65ABnuuX/Z71cAGe9Xf9nvYwAZ72N/2e9mABnvZv/Z74UAGe+Ff9nvkcAZ75H/2e+aABnvmn/Z750AGe+d/9nvnoAZ757/2e/" +
            "ZgBnv2f/Z7/yAGe/8/9nwAAAZ8Af/2fAMABnwDv/Z8BUAGfAc/9nwIAAZ8CT/2fApABnwKf/Z8C8AGfAv/9nwNAAZ8Db/2fA/ABn" +
            "wP//Z8EoAGfBL/9nwXgAZ8F7/2fBjABnwY//Z8GgAGfBo/9nwbwAZ8HD/2fB1ABnwe//Z8IQAGfCE/9nw2gAZ8Nr/2fDcABnw3P/" +
            "Z8OUAGfDm/9nw6AAZ8Oj/2fEQABnxEP/Z8RIAGfES/9nxFgAZ8Rj/2fEqABnxKv/Z8TMAGfEz/9nxQAAZ8UD/2fFtABnxbf/Z8Xk" +
            "AGfF5/9nxhQAZ8YX/2fGPABnxkP/Z8ZIAGfGS/9nxnwAZ8Z//2fGnABnxp//Z8a0AGfGt/9nxsQAZ8bH/2fG2ABnxvf/Z8ekAGfH" +
            "p/9nx8QAZ8fH/2fH5ABnx+f/Z8f4AGfH//9nyDQAZ8g3/2fIQABnyEf/Z8iIAGfIw/9nyNwAZ8kX/2fJHABnyUP/Z8lMAGfJe/9n" +
            "yZgAZ8ov/2fKOABnypP/Z8qYAGfKz/9nytQAZ8rX/2fK5ABnyuf/Z8rsAGfLI/9nyzgAZ8s7/2fLYABny4P/Z8uMAGfLj/9ny6QA" +
            "Z8ur/2fLwABny8P/Z8vIAGfLy/9ny9QAZ8vb/2fMGABnzBv/Z8xIAGfMS/9nzFgAZ8xb/2fMcABnzHP/Z8yIAGfMm/9nzMQAZ8zH" +
            "/2fM2ABnzNn/Z8zoAGfM7/9nzQQAZ80L/2fNKABnzS//Z800AGfNN/9nzWwAZ81v/2fNdABnzXv/Z82IAGfNi/9nzaIAZ82i/2fN" +
            "vABnzcv/Z83sAGfN7/9nzfgAZ84D/2fOLABnzi//Z86UAGfOl/9nzzAAZ88z/2fPaABnz2v/Z8+4AGfP1/9nz9wAZ8/f/2fP5ABn" +
            "z+v/Z9AMAGfQE/9n0BwAZ9Af/2fQKABn0DP/Z9FwAGfRc/9n0YgAZ9GL/2fRyABn0cv/Z9HQAGfR0/9n0dgAZ9Hb/2fSYABn0mP/" +
            "Z9KcAGfSv/9n0tgAZ9Lb/2fTLABn0y//Z9NgAGfTY/9n05wAZ9Of/2fTpABn06f/Z9PCAGfTw/9n09wAZ9Pj/2fT+ABn0/v/Z9QA" +
            "AGfUD/9n1CwAZ9Qz/2fUVABn1Ff/Z9RkAGfUZ/9n1GwAZ9Rv/2fUlABn1Jf/Z9SkAGfUp/9n1MQAZ9TL/2fU5ABn1Of/Z9T8AGfU" +
            "//9n1SgAZ9Vj/2fVhABn1b//Z9XEAGfVx/9n1eIAZ9Xj/2fV6ABn1en/Z9YwAGfWM/9n1lQAZ9ZX/2fW1ABn1tf/Z9bwAGfW9/9n" +
            "1xwAZ9cn/2fXLABn1zP/Z9dkAGfXZ/9n12wAZ9dv/2fXdABn13v/Z9eMAGfXj/9n2AQAZ9gv/2fYQABn2EP/Z9hsAGfYb/9n2IgA" +
            "Z9iL/2fYmABn2J3/Z9jgAGfY5/9n2PAAZ9k//2fZqABn2av/Z9m0AGfZz/9n2ggAZ9oX/2faHABn2l//Z9qyAGfas/9n2sAAZ9rb" +
            "/2fbGABn2yf/Z9tAAGfbQ/9n21QAZ9tn/2fbsABn27P/Z9u4AGfbu/9n3DAAZ9xD/2fcXABn3G//Z9x0AGfcm/9n3KAAZ9zL/2fc" +
            "8ABn3TP/Z91YAGfdz/9n3gAAZ94T/2feGABn3uv/Z97wAGfe9/9n3xAAZ9+H/2ffjABn37f/Z9+8AGfgA/9n4CgAZ+Av/2fgPABn" +
            "4D//Z+BQAGfgU/9n4NwAZ+Dr/2fhEgBn4RL/Z+FUAGfhV/9n4hAAZ+IT/2fiKABn4iv/Z+I4AGfiP/9n4lAAZ+JT/2fidABn4nX/" +
            "Z+KEAGfih/9n4pwAZ+Kf/2fitABn4rf/Z+LEAGfix/9n4zAAZ+Mz/2fjSABn41P/Z+NkAGfjZ/9n43gAZ+N7/2fjhABn44v/Z+PE" +
            "AGfjx/9n48wAZ+PP/2fj1ABn49f/Z+PkAGfj5/9n5AwAZ+QP/2fkWABn5Fv/Z+SIAGfki/9n5KAAZ+Sj/2fksABn5LP/Z+TMAGfk" +
            "0/9n5OQAZ+Tr/2flFABn5Rf/Z+U8AGflP/9n5YgAZ+WL/2fllABn5Zf/Z+WsAGflr/9n5dQAZ+Xf/2fl5ABn5ef/Z+XsAGfl8/9n" +
            "5gAAZ+YD/2fmHABn5h//Z+YsAGfmL/9n5mAAZ+Zj/2fmbgBn5m//Z+bEAGfmz/9n5tQAZ+bX/2fm7ABn5u//Z+cQAGfnF/9n50AA" +
            "Z+dH/2fntABn57v/Z+f0AGfn9/9n6AQAZ+gH/2fokABn6JP/Z+imAGfop/9n6LwAZ+i//2fo1ABn6Nf/Z+kEAGfpB/9n6SwAZ+kv" +
            "/2fpNABn6Tf/Z+loAGfpa/9n6YAAZ+mD/2fpiABn6Yv/Z+miAGfpo/9n6bIAZ+mz/2fp5ABn6ef/Z+oAAGfqA/9n6hQAZ+oX/2fq" +
            "OABn6jv/Z+qAAGfqg/9n6qwAZ+qv/2fqtABn6rf/Z+r0AGfq9/9n6zAAZ+sz/2frOABn6z//Z+tQAGfrV/9n62QAZ+tn/2frgABn" +
            "65f/Z+u4AGfru/9n68AAZ+vD/2fryABn68v/Z+vcAGfsY/9n7HQAZ+x1/2fseABn7Hv/Z+y4AGfsu/9n7NwAZ+zf/2fs6ABn7Ov/" +
            "Z+zwAGftD/9n7RgAZ+0f/2ftRABn7Uf/Z+1YAGftXf9n7ZgAZ+2b/2ftsABn7gf/Z+4QAGfuO/9n7lgAZ+5j/2fuhABn7of/Z+6M" +
            "AGfuk/9n7pgAZ+6Z/2fuoABn7r//Z+7EAGfux/9n7swAZ+7P/2fu/ABn7wP/Z+8sAGfvL/9n70QAZ+9H/2fvmABn75//Z++0AGfv" +
            "u/9n78AAZ+/H/2fvzABn79P/Z+/gAGfv4/9n7/QAZ+/3/2fwEABn8BP/Z/AkAGfwJ/9n8CoAZ/Ar/2fwSABn8Ev/Z/BUAGfwV/9n" +
            "8HwAZ/B//2fwrABn8K//Z/C8AGfwv/9n8MgAZ/DL/2fw9ABn8Pf/Z/EMAGfxD/9n8VwAZ/Fj/2fxoABn8aP/Z/GsAGfxrf9n8bgA" +
            "Z/G//2fx3ABn8d//Z/JAAGfyQ/9n8oAAZ/KH/2fyoABn8qP/Z/KoAGfys/9n8sgAZ/LL/2fy1ABn8tf/Z/LcAGfy3/9n8vAAZ/Lz" +
            "/2fziABn84v/Z/P8AGfz//9n9BAAZ/QT/2f0GgBn9Bv/Z/Q6AGf0V/9n9HQAZ/R3/2f0pABn9Kf/Z/ToAGf06/9n9PwAZ/T//2f1" +
            "FwBn9Rn/Z/U0AGf1N/9n9TwAZ/U//2f1UABn9VP/Z/V8AGf1g/9n9ggAZ/YP/2f2eABn9n//Z/aEAGf2h/9n9pgAZ/af/2f3qABn" +
            "97P/Z/e/AGf3v/9n98gAZ/fL/2f31ABn99f/Z/gAAGf4Af9n+EAAZ/hD/2f4ZABn+Gf/Z/h8AGf4f/9n+JgAZ/ib/2f4qABn+Kv/" +
            "Z/jAAGf4w/9n+NQAZ/jX/2f44ABn+OP/Z/kIAGf5D/9n+TQAZ/k3/2f5aABn+Wv/Z/mAAGf5g/9n+YgAZ/mL/2f5kABn+ZP/Z/mk" +
            "AGf5s/9n+bwAZ/nD/2f59ABn+ff/Z/n8AGf5//9n+iAAZ/oj/2f6aABn+mv/Z/p8AGf6f/9n+rQAZ/q3/2f6wABn+sP/Z/rYAGf6" +
            "2/9n+uAAZ/rj/2f67ABn+u//Z/r4AGf6//9n+yAAZ/sj/2f7VABn+1f/Z/tgAGf7Y/9n+3wAZ/uD/2f7oABn+6P/Z/vAAGf7w/9n" +
            "+8wAZ/vP/2f78ABn+/P/Z/wcAGf8H/9n/CQAZ/wn/2f8QABn/EP/Z/xgAGf8Y/9n/GgAZ/xr/2f8rABn/K//Z/zMAGf80/9n/OgA" +
            "Z/zr/2f8+ABn/Pv/Z/0EAGf9B/9n/TwAZ/0//2f9zABn/c//Z/3cAGf94/9n/egAZ/3r/2f+CABn/gv/Z/4UAGf+F/9n/kAAZ/5P" +
            "/2f+cABn/nP/Z/6wAGf+s/9n/rwAZ/6//2f+xABn/sT/Z/7cAGf+3/9n/0QAZ/9H/2f/WABn/1//Z/+IAGf/j/9n/7gAZ/+7/2f/" +
            "yABn/8v/Z//QAGf/0f9n/9QAZ//X/2f/5ABn/+f/agAAAGoAAP9qAAIAagAf/2oALABqAC//agBAAGoAf/9qAgAAagn//2oLAABq" +
            "Mv//ajQAAGo///9qSgAAakv//2pQAABqX///amwAAGp///9q4AAAau///230AABt9P//bgYAAG4H//9uEAAAbhP//24iKABuIi//" +
            "bigAAG4r//9uLAwAbiwP/24skABuLJ//bjAAAG4w//9uMwAAbjX//244AABuQf//bkgAAG5J//9uSwAAbkw//25MhABuTIf/bkyc" +
            "AG5Mn/9uTLgAbky7/25MwABuTX//blAAAG5b//9uXEQAblxH/25dIABuXT//bl4AAG5///9umAAAbp3//26lIABupT//bqYAAG6n" +
            "//9urMAAbq0v/26tQABurX//bq3AAG6t3/9usAAAbt///27kAABu5///buggAG7oP/9u7AAAbu3//27wAABvP///b0IAAG9C//9v" +
            "Q8AAb0PP/29EQABvRF//b0gAAG9P//9vVQAAb1X//29c+ABvXP//b3AAAG91//9vdsgAb3bP/293QABvd5//b3gAAG98//9vfgAA" +
            "b6f//2+qAABvqv//b6wAAG+3//9vugAAb7v//2/AAABv1///b90cAG/dHP9v3YAAb97//2/fBABv3xP/b9/wAG/f8/9v3/gAb9/7" +
            "/2/gAABv5///b+tgAG/rf/9v65wAb+u//3AAAABwQ///cEkAAHBL//9wUAAAcGf//3BtgABwbf//cG8AAHB1//9wegAAcIT//3CJ" +
            "MABwiTf/cMAAAHDD//9w4AAAcQn//3ELwABxC9//cQwAAHES//9xFegAcRXv/3EYAABxG///cR8AAHEf//9xLAAAcTP//3E0oABx" +
            "NL//cTTkAHE05/9xNgAAcTt//3E74ABxO+P/cT4AAHGB//9xgmAAcYJ3/3GEAABxj///ccAoAHHAKf9xwgAAccP//3HFZABxxWf/" +
            "ccgAAHHK//9xzAAAcc///3HQYABx0f//cdQAAHHUP/9x1GQAcdRn/3HUuABx1L//cdUAAHHVf/9x1gAAcdf//3HaAABx+///chwA" +
            "AHIc//9yH0AAch9H/3I2AAByN///cjwAAHJE//9yT0AAck9//3JQAABya///cm4AAHJuD/9ybkAAcm5//3JvAABybx//cm+gAHJv" +
            "v/9ycAAAcnd//3J3wAByd///coQAAHKE//9yhwAAcof//3KKAAByi///co1AAHKNR/9yjVAAco1X/3KNgAByjb//co6IAHKOj/9y" +
            "xAAAcsX//3LG+AByxv//ctAAAHL///9zGAAAcx3//3MfQABzH0//cyAAAHMj//9zKjgAcyo7/3MsAABzP///c0VAAHNFT/9zVAAA" +
            "c1Q//3NVwABzVf//c2QAAHNr//9zeAAAc3v//3N8EABzfB//c5QAAHOf//9zpkAAc6Zf/3OoAABzr///c7QAAHO3//9zuwAAc7sP" +
            "/3O+AABz7///dAAIAHQAD/90ABgAdAAf/3QBAAB0C///dA0AAHQN//90EAAAdB///3QyAAB0Mg//dDQAAHQ5//90OoAAdDqP/3Q6" +
            "0AB0Ot//dDwAAHQ///90QgAAdEJ//3REiAB0RI//dESwAHREt/90RQAAdEZ//3RMAAB0T///dFUAAHRV//90WZAAdFmf/3RZ8AB0" +
            "WfP/dFpQAHRaX/90WrgAdFq//3RfAAB0X///dHAAAHR1//90gAAAdMD//3TBEAB0wT//dMGwAHTBt/90wgAAdMT//3TFoAB0xaf/" +
            "dMYAAHTHn/90zAAAdMx//3TMhAB0zIf/dM0AAHTN//90zwAAdNP//3TUoAB01K//dNUsAHTVL/901UAAdNX//3TWIAB01k//dNaA" +
            "AHTb//904AAAdO///3TyAAB0+f//dPtAAHT7f/90/AAAdP3//3T+aAB0/m//dP6AAHT+//90/4AAdP///3UIAAB1D///dRUAAHUt" +
            "//91MAAAdTP//3U1MAB1NT//dTWwAHU1v/91OQAAdTp//3U7AAB1Sf//dUpAAHVKX/91SoAAdV///3VkAAB1Zf//dWcQAHVnH/91" +
            "ZygAdWcv/3VnSAB1Z0//dWeAAHVnj/91aKgAdWiv/3VqAAB1a///dXAAAHV3//91eEAAdXnH/3V6gAB1ev//dXwAAHWGv/91hwAA" +
            "db///3YYAAB2GiP/dhooAHYaZ/92GnAAdhqD/3YahQB2Go//dhqgAHYar/92GrwAdhr//3YcAAB2H///dkAAAHZC//92Q3AAdkN/" +
            "/3ZIAAB2Uf//dlQAAHZV//92WCAAdln//3Zb8AB2W///dmYQAHZmJ/92Z6QAdmez/3ZwAAB2fv//dn+AAHZ/n/92hAAAdof//3aQ" +
            "AAB2k///drIAAHay//92tAAAdrj//3a6AAB2vP//dr4AAHbBD/92wTAAdsE3/3bBYAB2wuP/dsLwAHbC9/92wwAAdsf//3bKAAB2" +
            "z///dtQAAHbV//9218AAdtf//3bgAAB25v//du8AAHbv//928gAAdvL//3b0AAB3Ah//dwKAAHcH//93CgAAdwp//3cPiAB3D4//" +
            "dxAAAHcQ//93EsAAdxLX/3cS4AB3F///dxtAAHcd//93HjAAdx4//3cfwAB3H9//dyAAAHcoT/93KIAAdyof/3cqgAB3Ko//dyrg" +
            "AHcq//93LAAAdy3//3cwAAB3N///dzkAAHc6//93O4AAdz7//3c/IAB3Pz//d0vQAHdL3/93TgAAd1D//3dS0AB3Ut//d1QAAHdb" +
            "//93YAAAd2f//3dsAAB3bf//d3AAAHeT//93lKAAd5S//3eXwAB3l///d6DIAHegz/93oXgAd6f//3ewAAB3v///d+gAAHfp//93" +
            "64AAd+u//3f4AAB3+///d/xgAHf8Z/93/PAAeA///3gYAAB4G///eB4AAHgx//94NAAAeDf//3hAAAB4R///eEggAHhIP/94SIAA" +
            "eEj//3hMAAB4V///eFgIAHhYD/94WgAAeFz//3heAAB4X///eIAAAHiH//94iBAAeIgX/3iIgAB4iL//eIkAAHiJf/94j4AAeI+f" +
            "/3jAAAB4////eQAIAHkAH/95BAAAeQX//3kIAAB5K///eS4AAHkuP/95LkwAeS5P/3kugAB5Mf//eTIIAHkyD/95MwAAeTP//3k0" +
            "oAB5NL//eTTQAHk0//95NrAAeTa3/3k2vAB5Nr//eTcAAHk3P/95OAAAeTp//3k6iAB5Oqf/eTsAAHk///95RAAAeUf//3lMAAB5" +
            "Tf//eU+AAHlPv/95WQAAeVn//3lbaAB5W2//eWSAAHllP/95ZdAAeWXf/3nAAAB5x///ecjAAHnIx/95yQAAecn//3nMAAB5z///" +
            "eeAAAHnv//95+AAAefv//3n/AAB5////egBAAHoA//96BAAAegh//3oIwAB6Cf//egqAAHoKhf96CogAegqJ/3oKpAB6CsP/egrI" +
            "AHoK2/96CuQAegrz/3oLAAB6C3//egwAAHoO//96MAAAejE//3ozAAB6M///ekAAAHph//96ZgAAemYP/3pmQAB6Zl//enAAAHpz" +
            "//96dwAAenf//3qAZAB6gGf/eoB4AHqAf/96iAAAeo///3qQgAB6kP//epjAAHqY//96nAAAep///3q8AAB6w///esYAAHrG//96" +
            "yCgAesgv/3rIQAB6yH//eskwAHrJP/96zAAAes///3rgAAB69///evgYAHr4H/96+DAAevg//3r/QAB6/0f/ewCAAHsAv/97BAAA" +
            "ew///3sxgAB7Mf//ezKgAHsyv/97NAAAezq//3s64AB7Pv//e0AAAHtif/97Y4AAe2Qf/3tlAAB7Zf//e2cAAHtnf/97bFgAe2xZ" +
            "/3tsgAB7bI//e2zQAHts3/97cAAAe4f//3uIUAB7iF//e4kAAHuL//97kAAAe6///3uwPAB7sD//e7BQAHuwX/97sQAAe7///3vE" +
            "AAB7xf//e8eAAHvH//97zgAAe8///3voAAB76///e/IAAHvyf/978sAAe/LH/3v0AAB79///e/kAAHv5//97/TQAe/01/3v+YAB7" +
            "/mf/fAZAAHwGf/98DgAAfBH//3wUAAB8F///fBzAAHwdf/98HwAAfB///3wocAB8KN//fCjwAHwo8/98KgAAfCr//3wvAAB8Lz//" +
            "fEAAAHxCf/98QwAAfE///3xYAAB8X///fGwIAHxsD/98bCgAfGwv/3xtYAB8bWf/fHAAAHx3//98fgAAfIf//3yTgAB8k///fJaJ" +
            "AHyWif98lwAAfJj//3ygAAB8p///fKwAAHyv//98wAAAfMH//3zEAAB8xP//fMU4AHzFP/98yAAAfM///3zcAAB88L//fPIAAHzy" +
            "//9888AAfPP//3z4AAB8+H//fPkAAHz7//98/gAAfP4//30fwAB9L///fTqAAH06//99PYAAfT4//31AAAB9Yv//fWgAAH1///99" +
            "qQAAfan//32rAAB9q///fdAAAH3QP/990gAAfdP//33VAAB91X//fdZgAH3Wf/991wAAfdc//33YAAB93///ff6AAH3+//+AbAAA" +
            "gGz//4EcAACBHP//gcwAAIHM//+B0wAAgdP//4ToAACE6P//hjHwAIYx8/+GrwAAhq///4k7OwCJOzv/iTtYAIk7W/+K/EgAivxJ" +
            "/4r8TgCK/E//ivxgAIr8Yf+K/HYAivx3/4r8ogCK/KP/iwU4AIsFP/+LBVAAiwVT/4sFXACLBV//iwVsAIsFb/+LBYAAiwWD/4sF" +
            "oACLBaP/iwXAAIsFw/+LBcwAiwXP/4sF1ACLBdf/iwX0AIsF9/+LCQAAiwn//4uBAACLgf//i5QAAIuU//+LmwAAi5v//4ufAACL" +
            "n///i6oAAIuq//+LsAAAi7D//4u3AACLt///i7oAAIu6//+LvQAAi73//4vEAACL1///i9kAAIvZ//+L2wAAi93//4vgAACL4P//" +
            "i+IAAIvj//+MSwAAjEv//4yPAACMj///jLMAAIyz//+MzQAAjM///4zSAACM0v//jOAAAIzg//+M7QAAjO3//4zwAACM8P//jPMA" +
            "AIzz//+M9gAAjPb//4z5AACM+v//jP8AAIz///+OVgAAjlb//49AAACPQP//kAAAAJAA//+QBwAAkAf//5AMAACQDP//kDBAAJAw" +
            "Q/+QMFgAkDBb/5AwnACQMJ//kDC0AJAwu/+QMMwAkDDX/5Aw3ACQMN//kDD8AJAw//+QNAAAkDT//5BPBACQTwX/kE8KAJBPC/+Q" +
            "TxwAkE8d/5BPKgCQTyv/kE82AJBPN/+QT0YAkE9H/5BPVACQT1X/kE92AJBPd/+QT5QAkE+V/5B7AACQe///kP8AAJD///+SOMAA" +
            "kjj//5LEOACSxDv/ksREAJLES/+SxFwAksRf/5LEcACSxHf/ksR8AJLEf/+URgAAlEb//5UpAACVKf//lgAAAJYA//+WcwAAlnP/" +
            "/5Z5AACWev//loGIAJaBi/+WgcAAloHD/5aB/ACWgf//looAAJaL//+WngAAlp7//5bfAACW3///lvIAAJbyC/+W8hwAlvIf/5by" +
            "LACW8jv/lvJMAJbyU/+W8lwAlvJj/5bycACW8nv/lvKYAJbyq/+W8rgAlvLD/5by1ACW8tf/lvLgAJby4/+W8ugAlvL7/5b4AACW" +
            "+P//lv8AAJb///+Xng4Al54P/5eeSACXnkn/l55OAJeeUf+XnnoAl557/5hogACYaP//mIgAAJiI//+ZAAAAmQD//5kDAACZA///" +
            "mSIAAJkl//+ZYwAAmWP//5llAACZZf//mXYAAJl3//+aCIAAmgj//50AAACdAP//nQoiAJ0KJf+dCnAAnQpx/50KdgCdCnf/nQqC" +
            "AJ0Kg/+dCtoAnQrd/50K9gCdCvf/nQ9KAJ0PS/+dD14AnQ9f/50PZACdD2n/nQ/IAJ0Pyf+dEgAAnRL//50UiACdFIn/nRTCAJ0U" +
            "w/+dFPYAnRT3/509AACdPf//nUIqAJ1CK/+dQkYAnUJH/51CWACdQl//nUKkAJ1Cpf+dQvQAnUL1/513CACddxP/nXccAJ13H/+d" +
            "d4QAnXen/513rACdd6//nXfAAJ13x/+dd/AAnXfz/513/ACdd///nXoAAJ16//+dlAAAnZT//52cAACdnP//nf8AAJ3///+ePAAA" +
            "njz//55PAACeT///noz8AJ6M//+fGwAAnxv//59LAACfS///n+IAAJ/i//+gE0wAoBNN/6ATUgCgE1P/oBPQAKAT2/+gFBAAoBQT" +
            "/6AUMACgFDP/oBSCAKAUg/+gFjoAoBY7/6AWUgCgFlP/oBaUAKAWlf+gFrwAoBa9/6AW4ACgFuH/oBbmAKAW5/+gFvQAoBb3/6AZ" +
            "CgCgGQ3/oBkUAKAZFf+gGcIAoBnD/6AeKACgHin/oB6UAKAel/+gHsIAoB7H/6Ae5gCgHuf/oLvfAKC73/+gu/wAoLv//6C/AACg" +
            "vwH/oL9oAKC/af+gv24AoL9v/6DKPACgyj//oMqUAKDKm/+gyqgAoMqr/6DK0gCgyv//oOxOAKDsT/+g7JQAoOyV/6DsogCg7KP/" +
            "oOzqAKDs6/+g7PoAoOz7/6D6DgCg+hP/oPoYAKD6Gf+g+lQAoPpV/6D6WgCg+lv/oPpmAKD6af+g+qAAoPqh/6D6qgCg+qv/oPrW" +
            "AKD61/+g+vwAoPr9/6F4AACheP//ob0AAKG9//+hzwAAoc///6H4FACh+BX/ofgqAKH4K/+h+FQAofhV/6H4XACh+F3/ofhsAKH4" +
            "cf+h+IgAofiJ/6IENgCiBDf/ogSIAKIEif+iBKgAogSp/6IExgCiBMf/ogTKAKIEy/+iBOYAogTn/6IOAACiDv//omkAAKJp//+j" +
            "AAAAowD//6MvBACjLwf/ozQcAKM0Hf+jNEwAozRN/6M0bACjNG3/ozT2AKM09/+jNQAAozUP/6M1JACjNUP/ozVYAKM1i/+jNaAA" +
            "ozWv/6M1vACjNb//ozXwAKM18/+jPT4Aoz0//6M9cQCjPXH/oz2yAKM9s/+jPcoAoz3L/6M91gCjPdf/o30AAKN9//+jgB4Ao4Af" +
            "/6OAmACjgJn/o4DsAKOA7f+jgP4Ao4D//6OOAACjjv//o7EAAKOx//+jswAAo7P//6PMAACjzP//o98cAKPfHf+j3yAAo98h/6Pf" +
            "fACj333/o9+tAKPfrf+j37IAo9+z/6Pf2ACj39n/o9/eAKPf3/+j4yIAo+Mj/6PjKACj4yn/o+NKAKPjUf+j5AAAo+T//6Q0AACk" +
            "NH//pWMEAKVjBP+lYyoApWMr/6VlBAClZQX/pWVCAKVlQ/+lZUYApWVH/6VlegClZXv/pWWQAKVlkf+lZaoApWWr/6Vl0AClZdH/" +
            "pWX0AKVl9f+mbwAApm///6eLAACni///p70AAKe9//+n3PQAp9z3/6igAACooP//qrMAAKqz//+rCAAAqw///6siAACrL///q1AA" +
            "AKtf//+raAAAq3///6vQAACr3///rFHAAKxR//+vAAAArxv//68eAACvH///ryoAAK8s//+vLgAAr1///69mAACvZv//r2qAAK9q" +
            "//+vb5AAr2+v/69vuACvb7v/r5IAAK+b//+vnmAAr55j/6+gAACvr///r7CcAK+wn/+vsLAAr7Cz/6+wvACvsL//r7IAAK+y//+v" +
            "uIAAr7i//6+5AACvv///tEwAALRP//+0VAAAtFb//7RYAAC0W///tF44ALReP/+0XmAAtF5v/7ReeAC0Xn//tF+AALR///+0gYAA" +
            "tIL//7SIAAC0j///tJQQALSUF/+0lJgAtJSf/7SU2AC0lP//tJWAALSVn/+0lqAAtJa//7SYAAC0r///tLJwALSyd/+0ssAAtLL/" +
            "/7S4AAC0vH//tL2UALS9l/+0yPwAtMv//7TQAAC00f//tNLUALTS1/+00uAAtNL//7TUAAC01f//tN7gALTf//+06QAAtOlf/7Tp" +
            "kAC06ZP/tOtAALTrX/+063AAtOtz/7YQkAC2EJf/thDAALYQ3/+2EgAAthJ//7YXuAC2F7//thfIALYXz/+2IAAAti///7YwYAC2" +
            "MH//tjEAALYyD/+2MnAAtjJ//7YzAAC2M///tjYAALY2f/+2NvQAtjb3/7Y9AAC2Pf//tlAAALZc//+2YAAAtpf//7adAAC2nf//" +
            "tqBAALagX/+2rgAAtq///7bIAAC2z///tuyAALbs//+27RgAtu0f/7buAAC27x//tvAAALb3//+2/gAAtv7//7b/PAC2/z//twAA" +
            "ALdH//+3TqAAt06n/7dOtAC3Trf/t1GsALdRr/+3UbQAt1G3/7dUAAC3Vf//t1uAALdbg/+3W4gAt1uf/7dcAAC3X///t4AAALeq" +
            "//+3rAAAt6///7e2AAC3th//t7gAALf///+8g4AAvIP//8AzrADAM63/wDO8AMAzvP/ANy4AwDcv/8A3RADAN0f/wGbMAMBmz//A" +
            "fJoAwHya/8CMgADAjIv/wIycAMCM1//AjPQAwIz1/8CQgADAkP//wMVxAMDFcf/A6CIAwOgj/8DoLADA6C//wXAAAMFw///BdwAA" +
            "wXcf/8avZADGr2f/x9Q5AMfUOf/KAGQAygBl/8oAegDKAHv/ygCwAMoAs//KA4AAygOB/8oEgADKBJ//ygT8AMoE///KBdAAygXb" +
            "/8oGBgDKBgf/ygZCAMoGQ//KBkgAygZJ/8oGVwDKBln/ygZcAMoGXf/KBmcAygZn/8oGbADKBmz/ygZuAMoGb//KBnIAygZy/8oG" +
            "sADKBr//ygbKAMoGy//KCAAAyggA/8oIAgDKCAX/yggMAMoIDP/KCBgAyggY/8oITQDKCE3/ygiAAMoIn//KCMAAygjP/8oJIADK" +
            "CSD/ygkiAMoJI//KCTAAygkx/8oJMwDKCTb/ygk5AMoJO//KCkAAygpP/8oKcADKCn//ygwBAMoMAv/KDBEAygwS/8oMSADKDEj/" +
            "ygxUAMoMVf/KDGAAygxg/8oMYgDKDGP/ygxqAMoMav/KDG8Aygxv/8oMdADKDHT/yg5AAMoOQf/KDkUAyg5F/8oOSQDKDkz/yg5O" +
            "AMoOT//KDlgAyg5Y/8oOYQDKDmH/yg5oAMoOaf/KDmwAyg5t/8oObwDKDm//yg5yAMoOc//KDnYAyg53/8oOfADKDn3/yg5/AMoO" +
            "f//KDoEAyg6B/8oOhwDKDoj/yg6VAMoOlf/KDpcAyg6X/8oOnQDKDp//yg6pAMoOsP/KDrgAyg65/8oO0ADKDtH/yg7VAMoO1f/K" +
            "DtsAyg7c/8oO3gDKDt//yg7hAMoO4//KDucAyg7n/8oO6wDKDu//yg72AMoO9v/KDvsAyg77/8oUQgDKFEL/yhRPAMoUT//KFFcA" +
            "yhRa/8oUXgDKFF//yhRyAMoUcv/KFHUAyhR1/8oUeADKFHj/yhR9AMoUf//KFTAAyhU//8oVgwDKFYT/yhWNAMoVjv/KFZMAyhWU" +
            "/8oVlgDKFZr/yhWcAMoVnP/KFvgAyhb//8obDADKGwz/yhsOAMobDv/KG4gAyhuJ/8ok4gDKJOL/yiYAAMomA//KJggAyiYP/8om" +
            "MADKJon/yiaMAMomj//KJpIAyiaT/8omlQDKJpz/yiaeAMomn//KJqQAyiaq/8omsADKJrH/yia4AMom///KKAQAyigF/8ooBwDK" +
            "KAf/yigPAMooD//KKIcAyiiI/8oojADKKIz/yiiPAMookf/KKJYAyiiW/8oomwDKKJz/yiieAMoon//KKKIAyiii/8opCADKKQn/" +
            "yikLAMopDf/KKYAAyimA/8opggDKKYP/yimYAMopn//KKcAAyinA/8opxADKKcv/yinwAMop///KK0wAyitP/8orkADKK5//yiwQ" +
            "AMosH//KLDAAyiwz/8osQwDKLEP/yixKAMosSv/KLGEAyixh/8osgQDKLIH/yiyEAMoshf/KLJIAyiyT/8otAADKLQL/yi0PAMot" +
            "H//KLhAAyi4S/8ouFADKLhX/yi4gAMouP//KLoAAyi6A/8ou4ADKLu//yi9SAMovU//KL2AAyi9v/8ovfgDKL37/yi+AAMovgP/K" +
            "L4IAyi+D/8o0IQDKNCL/yjQvAMo0L//KNI8AyjSP/8o1jADKNYz/yjWPAMo1j//KOcAAyjnP/8o51ADKOdv/yjnwAMo6AP/KOmgA" +
            "yjpr/8o6cADKOnP/yjp6AMo6e//KOwAAyjsB/8o71ADKO9f/yjvsAMo77P/KO/AAyjvw/8o8MADKPDf/yjxgAMo8Z//KPHAAyjx/" +
            "/8o8hADKPJ//yj1EAMo9R//KPUwAyj1P/8o9WADKPVv/yj17AMo9e//KPX8Ayj1//8o+cADKPnP/yj74AMo+/P/KPv8Ayj7//8o/" +
            "UADKP1//yj+gAMo/v//KP/gAyj/7/8o//QDKP/3/ykEAAMpBCf/KQWAAykFv/8pCqADKQqv/ykMAAMpDA//KRQQAykUH/8pFEADK" +
            "RR//ykYAAMpGH//KRmAAykZv/8pGwADKRs//ykcgAMpHL//KSCgAykgv/8pIUADKSF//ykhwAMpIf//KSYAAykmD/8pJ8ADKSf//" +
            "ykoIAMpKD//KSiQAykok/8pKKgDKSir/yko0AMpKNP/KSlAAykpf/8pK/gDKSv//ykvQAMpL3//KS/wAykv//8pM/ADKTP//yk1Q" +
            "AMpNV//KTVwAyk1f/8pOCADKTg//yk/gAMpP5//KT/gAyk/7/8pQwADKUM//ylEAAMpRA//KUbAAylG//8pT/ADKVBH/ylQWAMpU" +
            "Fv/KVBgAylQf/8pV0ADKVd//ylb5AMpW+f/KVvwAylb//8pXUADKV1//ylggAMpYI//KWQgAylkP/8pZYADKWWP/yllsAMpZb//K" +
            "WXcAyll3/8pZ6ADKWe//yloQAMpaH//KWiUAylol/8paYADKWn//ylrBAMpawf/KWsQAylrE/8pazQDKWs3/ylrgAMpa7//KWwAA" +
            "ylsD/8pbYADKW2//yluwAMpbv//KW+AAylv//8pcCADKXA//ylwwAMpcP//KXPwAylz//8pd/ADKXf//yl5KAMpeSv/KXlEAyl5R" +
            "/8peXADKXl//yl/wAMpf9//KYAAAynoH/8p6IADKeif/ynpAAMp6X//KenAAynp//8p6hADKeoT/yntgAMp7b//Ke3QAynt7/8p8" +
            "EADKfBv/yn1rAMp9a//KfW0Ayn1t/8p9cADKfX//yn2wAMp9v//KfwAAyn8H/8p/DADKfx//yn8oAMp/P//Kf3AAyn+f/8p/wADK" +
            "f8//yn/UAMp////KgdAAyoHQ/8qCAADKgh//yoInAMqCJ//KguAAyoL//8qDEADKgxf/yoMwAMqDP//Kg9AAyoPf/8qFIADKhS//" +
            "yoY6AMqGOv/KhoAAyoaP/8qG0ADKht//yogwAMqIP//KiEgAyohJ/8qIUADKiFH/yojQAMqI7//KiPgAyoj7/8qJ5wDKief/yoyM" +
            "AMqMn//KjaAAyo2//8qOEADKjh//yo8EAMqPB//KjxAAyo8v/8qPOADKjz//yo9kAMqPa//KkMQAypDH/8qSoADKkq//ypK4AMqS" +
            "uv/KkrwAypK//8qSxADKks//ypOQAMqTn//KlCAAypQv/8qUQADKlH//ypUgAMqVP//KlaAAypW//8qV4ADKlf//ypYQAMqWL//K" +
            "ljgAypY7/8qWwADKls//ypbgAMqXA//KlyEAypch/8qXgADKl5//ypiwAMqYv//KmQAAypkD/8qZBwDKmQf/ypkwAMqZP//KncAA" +
            "yp3f/8qeoADKnr//yp7yAMqe8v/KoIwAyqCP/8qgnADKoJ//yqCwAMqgv//KokMAyqJD/8qiSwDKokv/yqQAAMqkD//KpGAAyqR/" +
            "/8qlsADKpb//yqXQAMql3//Kpe8AyqXx/8ql8wDKpfP/yqX1AMql9f/KpfsAyqX//8qm4ADKpv//yqhQAMqoU//KqIAAyqiP/8qo" +
            "oADKqL//yqqAAMqqn//KqtgAyqr//8qr2ADKq9//yqvoAMqr6P/Kq+sAyqvr/8qsAADKrAP/yqwHAMqsB//KrQAAyq0D/8qtBgDK" +
            "rQb/yq0IAMqtD//KrXAAyq1z/8qt4ADKrf//yq5AAMquT//KrnwAyq5//8qw4ADKsP//yrOgAMqzr//Ks/AAyrP//8q0gADKtJ//" +
            "yrTQAMq01//KtQgAyrUL/8q1HADKtR//yrVwAMq1f//KtiAAyrYv/8q2wADKtt//yr0AAMq9P//KvVAAyr1f/8q9uADKvb//yr8A" +
            "AMq/AP/Kv0QAyr9f/8rAAADKz///ywAEAMsAB//LAAoAywAL/8sAEgDLABL/ywAYAMsAGP/LACoAywAr/8sALQDLAC//ywBRAMsA" +
            "U//LAFoAywBb/8sAYADLAGH/ywBoAMsAb//LAHIAywBz/8sAegDLAHr/ywCAAMsAgP/LAIIAywCH/8sAiQDLAIn/ywCOAMsAjv/L" +
            "AJAAywCQ/8sAkgDLAJL/ywCUAMsAlP/LAJYAywCY/8sAsQDLALH/ywDgAMsA4P/LAQQAywEH/8sBEgDLARL/ywEaAMsBG//LAUEA" +
            "ywFD/8sBRgDLAUf/ywFMAMsBTf/LAVoAywFa/8sBYQDLAWf/ywFsAMsBbP/LAf0AywH+/8sCQADLAkf/ywJJAMsCSf/LAnAAywJ3" +
            "/8sCfgDLAn//ywKMAMsCjP/LApYAywKW/8sCmADLAp3/ywKgAMsCp//LArQAywK1/8sCxADLAsX/ywLRAMsC0f/LAtYAywLX/8sC" +
            "4gDLAuP/ywLlAMsC5f/LAuwAywLt/8sDRADLA0T/ywNIAMsDSf/LA0sAywNL/8sDUADLA1f/ywNgAMsDY//LA2kAywNp/8sDcADL" +
            "A3j/ywN7AMsDe//LA4cAywOH/8sDiwDLA4v/ywOPAMsDj//LBIQAywSG/8sElwDLBJv/ywSuAMsEr//LBLQAywS0/8sEugDLBLr/" +
            "ywTNAMsEzf/LBNAAywTT/8sE4wDLBOP/ywTmAMsE5//LBQQAywUF/8sFBwDLBQn/ywULAMsFC//LBRUAywUW/8sFLADLBSz/ywUu" +
            "AMsFL//LBTQAywU5/8sFPADLBT3/ywVyAMsFc//LBXYAywV2/8sFeADLBXj/ywWsAMsFrP/LBbQAywW2/8sFuQDLBbr/ywW8AMsF" +
            "vv/LBcMAywXD/8sF1gDLBdf/ywXaAMsF2//LBoMAywaD/8sGiADLBoj/ywaKAMsGi//LBo4AywaO/8sGlgDLBpf/ywadAMsGnf/L" +
            "Bp8Aywaf/8sG4ADLBu//ywb4AMsG+f/LB4EAyweB/8sHigDLB4v/yweTAMsHk//LB5YAyweX/8sHngDLB57/ywfAAMsHwf/LB8gA" +
            "ywfI/8sIAADLCAD/ywgIAMsICP/LCBcAywgX/8sIRgDLCEb/ywhSAMsIUv/LCFYAywhX/8sIWwDLCFv/ywhuAMsIb//LCHMAywhz" +
            "/8sIpgDLCKf/ywipAMsIqf/LCK0Aywit/8sIuADLCLj/ywi6AMsIu//LCL4AywjA/8sIxQDLCMf/ywjLAMsIy//LCNEAywjX/8sI" +
            "2QDLCNn/ywjcAMsI3P/LCSAAywkg/8sJJADLCSX/ywk5AMsJOf/LCT8Aywk//8sJQQDLCUH/ywlGAMsJSP/LCUsAywlN/8sJYADL" +
            "CWX/ywlsAMsJbP/LCZ4Aywme/8sKIgDLCiL/ywo4AMsKOP/LCkoAywpL/8sKVADLClj/ywpfAMsKX//LCn0Aywp9/8sLRgDLC0b/" +
            "ywtMAMsLT//LC1IAywtS/8sLVADLC1f/ywtkAMsLZ//LC20Aywtt/8sLdQDLC3X/ywt6AMsLev/LC34Aywt+/8sLiADLC4v/ywuN" +
            "AMsLj//LC7QAywu3/8sL0ADLC9P/ywwQAMsMEP/LDBMAywwT/8sMGADLDBj/yww5AMsMOf/LDEEAywxC/8sMRgDLDEf/ywxXAMsM" +
            "V//LDGQAywxl/8sMZwDLDGf/ywxyAMsMcv/LDHYAywx2/8sMggDLDIL/ywyJAMsMif/LDMQAywzH/8sM0wDLDNP/ywzbAMsM2//L" +
            "DOIAywzi/8sM8ADLDPP/yw0SAMsNEv/LDRgAyw0Y/8sNLADLDS3/yw1YAMsNWf/LDVwAyw1f/8sNrQDLDa3/yw3gAMsN4f/LDeMA" +
            "yw3j/8sN6QDLDen/yw4YAMsOG//LDiEAyw4h/8sOOADLDjj/yw49AMsOPv/LDmgAyw5o/8sOcgDLDnP/yw52AMsOdv/LDqIAyw6i" +
            "/8sOwADLDsD/yw7CAMsOw//LDtYAyw7W/8sO5wDLDuf/yw72AMsO9v/LDwAAyw8P/8sPFADLDxb/yw9XAMsPWf/LD2kAyw9p/8sP" +
            "cADLD3f/yw+CAMsPg//LD5UAyw+V/8sPlwDLD5f/yw+cAMsPn//LD64Ayw+u/8sP4wDLD+P/yw/oAMsP8f/LD/YAyw/2/8sQCgDL" +
            "EAr/yxAMAMsQDf/LEBAAyxAX/8sQGwDLEBv/yxAmAMsQJv/LEDEAyxAz/8sQOgDLEDr/yxA/AMsQP//LEIUAyxCF/8sQoQDLEKL/" +
            "yxC6AMsQu//LEOQAyxDk/8sQ7gDLEO7/yxDwAMsQ8P/LEPUAyxD1/8sRAgDLEQL/yxESAMsREv/LERwAyxEc/8sRJwDLESf/yxE4" +
            "AMsROP/LEUoAyxFL/8sRWADLEVn/yxGIAMsRiP/LEaQAyxGk/8sRuwDLEbv/yxG+AMsRv//LEecAyxHn/8sR6QDLEen/yxH4AMsR" +
            "+f/LEf8AyxH//8sSAgDLEgT/yxIHAMsSB//LEh8AyxIf/8sSJQDLEiX/yxIwAMsSMf/LEjQAyxI0/8sSSADLEkv/yxJQAMsSUf/L" +
            "ElcAyxJX/8sSZADLEmX/yxJpAMsSaf/LEmsAyxJr/8sSbgDLEm7/yxKBAMsSgf/LEoMAyxKF/8sSkADLEpD/yxKZAMsSmf/LEscA" +
            "yxLH/8sS0ADLEtD/yxLTAMsS0//LEtcAyxLX/8sTAQDLEwH/yxMSAMsTEv/LExgAyxMY/8sTHgDLEx7/yxMgAMsTJ//LEykAyxMp" +
            "/8sTLADLEy7/yxM6AMsTOv/LEzwAyxM9/8sTQADLE0D/yxNEAMsTRP/LE0gAyxNI/8sTZQDLE2X/yxNvAMsTb//LE4MAyxOD/8sT" +
            "hQDLE4X/yxOQAMsTkP/LE5MAyxOT/8sTlQDLE5X/yxOcAMsTnP/LE7AAyxOw/8sTsgDLE7P/yxPQAMsT0P/LE+QAyxPn/8sT6QDL" +
            "E+n/yxPyAMsT8v/LE/gAyxP5/8sT/wDLE///yxQRAMsUEf/LFCgAyxQp/8sULADLFCz/yxQwAMsUMP/LFD0AyxQ9/8sUQQDLFEH/" +
            "yxRUAMsUVf/LFFkAyxRZ/8sUagDLFGv/yxRzAMsUc//LFHUAyxR3/8sUegDLFHr/yxR+AMsUf//LFIcAyxSP/8sUlgDLFJb/yxTm" +
            "AMsU5v/LFOgAyxTo/8sU7ADLFOz/yxUAAMsVAv/LFQgAyxUI/8sVCgDLFQr/yxUSAMsVEv/LFSEAyxUi/8sVKQDLFSn/yxUsAMsV" +
            "LP/LFUQAyxVE/8sVUgDLFVL/yxVgAMsVY//LFXwAyxV8/8sViADLFYn/yxWRAMsVkf/LFc4AyxXO/8sWGADLFhj/yxYcAMsWHf/L" +
            "Fh8AyxYf/8sWRADLFkT/yxZMAMsWTP/LFk4AyxZO/8sWVADLFlT/yxZXAMsWV//LFlwAyxZf/8sWYwDLFmP/yxZqAMsWav/LFnoA" +
            "yxZ7/8sWgwDLFoP/yxajAMsWo//LFqYAyxam/8sWqgDLFqr/yxawAMsWt//LFsIAyxbC/8sW8gDLFvP/yxb1AMsW9v/LFvwAyxb9" +
            "/8sXAADLFwD/yxcvAMsXL//LFz0Ayxc//8sXSQDLF0n/yxdVAMsXVf/LF1wAyxdf/8sXYgDLF2L/yxdrAMsXa//LF3AAyxdw/8sX" +
            "ggDLF4L/yxeMAMsXjf/LF6wAyxes/8sXtgDLF7b/yxe6AMsXu//LF8AAyxfA/8sXxQDLF8b/yxfMAMsXz//LF+AAyxfg/8sX4gDL" +
            "F+f/yxf5AMsX+f/LF/sAyxf7/8sYDQDLGA3/yxgSAMsYEv/LGBsAyxgb/8sYKwDLGCv/yxg4AMsYOP/LGDoAyxg6/8sYQwDLGEP/" +
            "yxhKAMsYSv/LGE8AyxhR/8sYVADLGFb/yxhaAMsYWv/LGG8Ayxhw/8sYdADLGHT/yxh6AMsYe//LGJEAyxiR/8sYmADLGJn/yxid" +
            "AMsYnf/LGKEAyxih/8sYpwDLGKf/yxi6AMsYu//LGMcAyxjH/8sYygDLGMr/yxjUAMsY1f/LGNkAyxjZ/8sY2wDLGNv/yxj0AMsY" +
            "9P/LGRMAyxkV/8sZLgDLGS7/yxkwAMsZN//LGUAAyxlB/8sZWwDLGVv/yxljAMsZZP/LGWoAyxlq/8sZgwDLGYP/yxmHAMsZh//L" +
            "GYoAyxmK/8sZkwDLGZP/yxmZAMsZm//LGaQAyxmk/8sZpgDLGab/yxmuAMsZr//LGbQAyxm0/8sZtgDLGbb/yxm/AMsZv//LGccA" +
            "yxnI/8sZygDLGcv/yxnQAMsZ3//LGeUAyxnl/8sZ6wDLGez/yxnyAMsZ8v/LGgwAyxoM/8saIgDLGiL/yxoxAMsaMv/LGjcAyxo5" +
            "/8saPADLGjz/yxpBAMsaQf/LGkQAyxpE/8saTADLGkz/yxpQAMsaUP/LGlQAyxpU/8saYQDLGmH/yxpmAMsaZ//LGnMAyxp0/8sa" +
            "gQDLGoH/yxqPAMsakP/LGpQAyxqV/8samgDLGpr/yxqeAMsan//LGqoAyxqq/8sarQDLGq3/yxqwAMsasP/LGrkAyxq5/8saygDL" +
            "Gsv/yxrSAMsa0v/LGtYAyxrW/8sa3gDLGt7/yxrgAMsa4P/LGuQAyxrk/8sa6ADLGuj/yxsAAMsbAP/LGwoAyxsK/8sbDwDLGxD/" +
            "yxsUAMsbFP/LGxYAyxsX/8sbKADLGyj/yxstAMsbLf/LGzUAyxs1/8sbQQDLG0L/yxtRAMsbUf/LG1gAyxtY/8sbZgDLG2b/yxtt" +
            "AMsbbf/LG3UAyxt1/8sbeQDLG3v/yxt9AMsbff/LG8gAyxvI/8sbygDLG8r/yxvpAMsb6f/LG/EAyxvx/8sb+gDLG/r/yxwKAMsc" +
            "Cv/LHAwAyxwM/8scIQDLHCP/yxwrAMscLP/LHDYAyxw2/8scOADLHDj/yxxJAMscSv/LHEwAyxxM/8scVgDLHFb/yxxYAMscWP/L" +
            "HHAAyxxw/8scgwDLHIP/yxyIAMsciP/LHIwAyxyM/8sckQDLHJH/yxylAMscpf/LHKkAyxyq/8scsgDLHLP/yxy5AMscuf/LHLsA" +
            "yxy7/8scxADLHMT/yxziAMsc4//LHO8Ayxzv/8sdAgDLHQL/yx0IAMsdCf/LHQ0Ayx0O/8sdHADLHRz/yx0uAMsdLv/LHTkAyx05" +
            "/8sdPQDLHT3/yx0/AMsdP//LHUUAyx1F/8sdSQDLHUn/yx1RAMsdUf/LHVoAyx1a/8sdXwDLHV//yx1kAMsdZP/LHWcAyx1n/8sd" +
            "cADLHXD/yx14AMsde//LHbYAyx23/8sduwDLHbv/yx29AMsdvv/LHc0Ayx3N/8sd0gDLHdL/yx3ZAMsd2f/LHeMAyx3j/8sd5wDL" +
            "Hef/yx3pAMsd6v/LHfgAyx34/8sd/gDLHf//yx4QAMseEf/LHhkAyx4Z/8seGwDLHhv/yx4dAMseHf/LHkIAyx5C/8seUQDLHlH/" +
            "yx5XAMseV//LHm8Ayx5v/8seeQDLHnn/yx57AMsee//LHpgAyx6Y/8senADLHpz/yx6iAMseov/LHq0Ayx6t/8serwDLHq//yx67" +
            "AMseu//LHsIAyx7C/8se2QDLHtn/yx7cAMse3P/LHt4Ayx7e/8se6ADLHun/yx7rAMse6//LHvAAyx7x/8se9gDLHvb/yx76AMse" +
            "+//LHy0Ayx8u/8sfMQDLHzH/yx8zAMsfM//LHzYAyx83/8sfRQDLH0X/yx9IAMsfSP/LH1AAyx9Q/8sfVQDLH1X/yx9hAMsfYf/L" +
            "H2kAyx9q/8sfbADLH23/yx98AMsffP/LH6IAyx+i/8sfrgDLH67/yx+xAMsfsf/LH7UAyx+1/8sfuwDLH7v/yx+9AMsfvf/LH8wA" +
            "yx/M/8sf3ADLH9z/yx/eAMsf3//LH+EAyx/h/8sf5QDLH+X/yx/4AMsf+f/LH/0Ayx/9/8sgFADLIBT/yyAwAMsgMf/LIDgAyyA4" +
            "/8sgPADLIDz/yyA+AMsgPv/LIEQAyyBF/8sgTADLIEz/yyBRAMsgUf/LIFQAyyBV/8sgXwDLIF//yyBmAMsgZv/LIGkAyyBp/8sg" +
            "ggDLIIL/yyCFAMsghf/LIIwAyyCM/8sgmADLIJj/yyC6AMsgu//LIMAAyyDA/8sgxADLIMT/yyDLAMsgzf/LINQAyyDU/8shBADL" +
            "IQT/yyEHAMshD//LIRUAyyEV/8shGgDLIRr/yyEgAMshIP/LIT8AyyFA/8shQwDLIUT/yyFJAMshSf/LIU8AyyFP/8shZADLIWT/" +
            "yyF6AMshev/LIYEAyyGB/8shgwDLIYP/yyGRAMshkf/LIZwAyyGc/8shngDLIZ//yyGuAMshrv/LIbkAyyG5/8shyADLIcj/yyHK" +
            "AMshzP/LIc4AyyHP/8sh1gDLIdf/yyHgAMsh4v/LIekAyyHp/8sh8wDLIfP/yyH6AMsh+v/LIgQAyyIE/8siFQDLIhX/yyIbAMsi" +
            "G//LIicAyyIn/8siMADLIjH/yyI2AMsiNv/LIjgAyyI5/8siQwDLIkP/yyJFAMsiRf/LIkwAyyJM/8siXADLIlz/yyJqAMsiav/L" +
            "InEAyyJx/8sikwDLIpP/yyKWAMsilv/LIpgAyyKZ/8sioQDLIqL/yyK7AMsiu//LIsAAyyLH/8sizADLIs//yyLoAMsi6P/LIvAA" +
            "yyLw/8si8gDLIvL/yyL1AMsi9f/LIvsAyyL7/8s3AgDLNwT/yzcKAMs3Cv/LNw0AyzcN/8s3FgDLNxb/yzceAMs3Hv/LN10Ayzdd" +
            "/8s3ZQDLN2X/yzdtAMs3bv/LN3QAyzd1/8s3dwDLN3f/yzeAAMs3gf/LN5IAyzeT/8s3wADLN8D/yzfEAMs3xP/LN9oAyzfb/8s3" +
            "3QDLN93/yzfgAMs34P/LOAEAyzgB/8s4BADLOAT/yzgMAMs4DP/LOBgAyzgY/8s4JgDLOCb/yzgoAMs4KP/LOC4Ayzgu/8s4MADL" +
            "ODf/yzhEAMs4Rf/LOFIAyzhV/8s4XwDLOF//yzhuAMs4bv/LOHkAyzh5/8s4oQDLOKH/yzipAMs4qf/LOKwAyzit/8s4rwDLOK//" +
            "yzi3AMs4t//LOLkAyzi5/8s4uwDLOLv/yzjAAMs4wP/LOMYAyzjG/8s4yQDLOMn/yzjQAMs40v/LONYAyzjW/8s42ADLONj/yzjj" +
            "AMs45P/LOOcAyzjo/8s48ADLOPD/yzj8AMs4/P/LOP4Ayzj+/8s5BQDLOQb/yzkMAMs5Df/LORwAyzkc/8s5JwDLOSf/yzkuAMs5" +
            "Lv/LOToAyzk6/8s5PQDLOT3/yzlCAMs5Qv/LOUUAyzlH/8s5SQDLOUn/yzlaAMs5Wv/LOWUAyzll/8s5bQDLOW3/yzl7AMs5e//L" +
            "OZ0Ayzmd/8s5yADLOcj/yznKAMs5yv/LOc4AyznO/8s53gDLOd7/yzngAMs57//LOfYAyzn3/8s5+QDLOfn/yzn9AMs5///LPgIA" +
            "yz4C/8s+gwDLPoP/yz6LAMs+i//LPqEAyz6h/8s+xQDLPsX/yz7kAMs+5//LPuoAyz7q/8s+9gDLPvb/y0ygAMtMo//LTKgAy0yr" +
            "/8tM0ADLTNv/y0zwAMtM9//LTbQAy023/8tOMADLTj//y06cAMtOn//LTwAAy08P/8tPIADLTy//y1AEAMtQBf/LUCAAy1Av/8tQ" +
            "OQDLUDn/y1CBAMtQgf/LUIQAy1Cf/8tRAADLUQf/y1EQAMtRH//LUfQAy1H3/8tSAADLUgH/y1IQAMtSF//LUnAAy1J//8tS4ADL" +
            "Uu//y1MAAMtTA//LUwgAy1MP/8tTOADLUz//y1PgAMtT7//LVgAAy1Z//8tW+gDLVvr/y1b+AMtW///LWCAAy1g//8tYwADLWN//" +
            "y1kIAMtZD//LWWQAy1ln/8tZhQDLWYX/y1mIAMtZi//LWZAAy1mQ/8taCADLWg//y1qAAMta3//LWyAAy1s//8tbYADLW2//y1t4" +
            "AMtbf//LXAAAy1wD/8tcBgDLXAb/y1ygAMtcv//LXQAAy14f/8tfAADLXwf/y19gAMtfv//LX8gAy1/T/8tf4ADLX///y2MIAMtj" +
            "H//LY1AAy2Nf/8tkIADLZDf/y2Q6AMtkOv/LZDwAy2Q8/8tkPwDLZD//y2RQAMtkf//LZMAAy2TP/8toIADLaC//y2lgAMtpn//L" +
            "awAAy2t//8tuoADLbr//y27QAMtu3//LbugAy27q/8tyUADLcl//y3L0AMty9//LdsAAy3bf/8t28QDLdvH/y3b4AMt2+//LdxgA" +
            "y3cj/8t3UADLd1P/y3dVAMt3Vf/Ld3EAy3f//8t7OgDLezr/y4AgAMuAP//LgGAAy4B//8uA4ADLgOf/y4EIAMuBD//LgiAAy4I/" +
            "/8uEIADLhD//y4bwAMuG9//Lh2AAy4d//8uHoADLh6//y47bAMuO2//LjuAAy47//8uQYADLkH//y5EAAMuRH//LlAAAy5RT/8uU" +
            "VgDLlFf/y5VcAMuVX//LmEAAy5hf/8uYgADLmJ//y5kAAMuZA//LnMAAy5z//8ueEADLnhf/y6A0AMugN//LoGgAy6Bv/8uggQDL" +
            "oIH/y6DAAMug3//LoQAAy6ED/8uhtwDLobf/y6HAAMuh3//LpqAAy6a//8unHADLpx//y6gAAMuoH//LqjoAy6o7/8urAADLqwP/" +
            "y6vQAMur0P/Lq+AAy6vv/8uuBADLrgT/y64GAMuuB//LrmAAy65//8uvgADLr5//y6/AAMuwX//LsKgAy7Cv/8u4UADLuF//y7m9" +
            "AMu5vf/Lu6AAy7u//8u9AADLvQH/y70GAMu9B//LvXAAy71z/8u9wADLvd//y73wAMu98//LvmAAy75v/8u++QDLvvn/y78AAMu/" +
            "Av/LvwUAy78F/8u/BwDLvwf/y78QAMu/H//Lv0AAy79//8u/hQDLv4X/y7+QAMu/n//LwAAAy8Af/8vB4ADLwf//y8J4AMvCf//L" +
            "w0AAy8Nf/8vDcADLw3f/y8OAAMvED//LxBwAy8Qf/8vJtQDLybb/y8rsAMvK7//LzUAAy81f/8vNgADLzf//y89AAMvP///L0CAA" +
            "y9A//8vR4ADL0f//y9QAAMvUD//L1FAAy9Rf/8vX6ADL1+//y9mkAMvZp//L3xAAy98X/8w0vwDMNL//0gIAANICH//SBQAA0gUf" +
            "/9IFOADSBT//0gWAANIFn//SBzgA0gc//9IMAADSDf//0g5AANIOX//SDnAA0g+//9IQaADSEGv/0hCAANIQv//SFQAA0hb//9IX" +
            "IADSFz//0hkAANIv///SMwAA0jX//9I4wADSON//0kgAANJO///ST0AA0k9//9JP4ADST///0lIAANJT///SV0gA0ldJ/9JXcgDS" +
            "V3P/0leAANJXv//SucAA0rn//9LAYADSwH//00AAANNH///TUAAA02f//9OIAADTp///1EAAANRAf//UgYAA1IH//9oAAADaH///" +
            "2jgAANpj///aZFgA2mT//9poAADabf//2rnAANq53//aufAA2rn3/9rAAADaz///2vAAANr3///a+QAA2vn//9tIAADbSP//21IA" +
            "ANtS///bU4AA21P//9taRADbWk//24AAANuf///b2AAA2+///9vyAADb9///3GXAANxl///ccAAA3HP//9yYgADcmP//3JoAANyb" +
            "///cnvAA3J7z/9ygAADcz///3OcAANznP//c54AA3Of//9zoQADc6H//3OoAANzq///c8gAA3PP//9z3iADc94//3PgAANz8///d" +
            "AAAA3Qy//90NAADdD///3XoAAN17///dgIAA3YP//92F4ADdhf//3YgAAN2J///drAAA3bf//93AAADdx8//3cfgAN3v///eEAAA" +
            "3l///959AADeff//3n6AAN5+///egAAA3o///96gAADeo///3qgAAN7f///e8AAA3vn//98AAADfD///3xQAAN8V///fG7gA3xu7" +
            "/98d0ADfHdP/3x38AN8d///fQAAA33X//994gADfeP//33mAAN95///fe9AA34H//9+QAADfo///36YAAN+n///fwAAA38H//9/G" +
            "AADfx///38kAAN/L///f0AAA39f//9/cAADf3f//39+wAN/fz//f8AAA3/v//9/8gADf/P//3/4AAN//f//f/+wA3//v/9///ADf" +
            "//3/";

        static uint[] Build()
        {
            var bytes = Convert.FromBase64String(Raw);
            var arr = new uint[bytes.Length / 4];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = ((uint)bytes[i * 4] << 24) | ((uint)bytes[i * 4 + 1] << 16) |
                         ((uint)bytes[i * 4 + 2] << 8) | bytes[i * 4 + 3];
            return arr;
        }

        /// <summary>这个 IP 属于国内分配的地址段吗？</summary>
        public static bool Contains(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();

            // 私网 / 回环 / 链路本地 / CGNAT：都按「不该走境外」处理
            if (b[0] == 10 || b[0] == 127) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

            uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

            // _ranges 是 [start0, end0, start1, end1, ...]，按 start 升序
            int lo = 0, hi = _ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                uint s = _ranges[mid * 2], e = _ranges[mid * 2 + 1];
                if (v < s) hi = mid - 1;
                else if (v > e) lo = mid + 1;
                else return true;
            }
            return false;
        }

        public static int Count { get { return _ranges.Length / 2; } }
    }
}
