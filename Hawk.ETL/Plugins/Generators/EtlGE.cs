﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls.WpfPropertyGrid.Annotations;
using System.Windows.Controls.WpfPropertyGrid.Attributes;
using System.Windows.Controls.WpfPropertyGrid.Controls;
using Hawk.Core.Connectors;
using Hawk.Core.Utils;
using Hawk.Core.Utils.Logs;
using Hawk.Core.Utils.Plugins;
using Hawk.ETL.Interfaces;
using Hawk.ETL.Managements;
using Hawk.ETL.Plugins.Transformers;
using Hawk.ETL.Process;

namespace Hawk.ETL.Plugins.Generators
{
    public class ETLBase : ToolBase, INotifyPropertyChanged
    {
        protected readonly IProcessManager processManager;
        private string _etlSelector;
      

        public ETLBase()
        {
            processManager = MainDescription.MainFrm.PluginDictionary["模块管理"] as IProcessManager;
            ETLSelector = new TextEditSelector {GetItems = this.GetAllETLNames()};
            ETLRange = "";
            Column = "column";
            Enabled = true;
        }
   
        [LocalizedCategory("2.调用选项")]
        [LocalizedDisplayName("子任务-选择")]
        [PropertyOrder(0)]
        [LocalizedDescription("输入要调用的子任务的名称")]
        public TextEditSelector ETLSelector { get; set; }

        [LocalizedCategory("2.调用选项")]
        [LocalizedDisplayName("调用范围")]
        [PropertyOrder(1)]
        [LocalizedDescription("设定调用子任务的模块范围，例如2:30表示从第2个到第30个子模块将会启用，其他的模块不启用，2:-1表示从第3个到倒数第二个启用，其他不启用，符合python的slice语法")]
        public string ETLRange { get; set; }


        protected SmartETLTool etl { get; set; }

    
     


        public override bool Init(IEnumerable<IFreeDocument> datas)
        {
            if (string.IsNullOrEmpty(ETLSelector.SelectItem))
                return false;
            etl =
                processManager.CurrentProcessCollections.FirstOrDefault(d => d.Name == ETLSelector.SelectItem) as SmartETLTool;
            if (etl != null)
            {
                return true;
            }

            var task = processManager.CurrentProject.Tasks.FirstOrDefault(d => d.Name == ETLSelector.SelectItem);
            if (task == null)

            {
                throw new NullReferenceException($"can't find a ETL Module named {ETLSelector}");
            }

            ControlExtended.UIInvoke(() => { task.Load(false); });



            etl.InitProcess(true);
            return etl != null;
        }

    

        protected IEnumerable<IColumnProcess> GetProcesses()
        {

            var range = ETLRange.Split(':');
            var start = 0;
            if (etl == null)
                yield break;

            var end = etl.CurrentETLTools.Count;
            if (range.Length == 2)
            {
                try
                {
                    start = int.Parse(range[0]);
                    if (start < 0)
                        start = etl.CurrentETLTools.Count + start;
                    end = int.Parse(range[1]);
                    if (end < 0)
                        end = etl.CurrentETLTools.Count + end;
                }
                catch (Exception ex)
                {
                    XLogSys.Print.Error("子任务范围表达式错误，请检查:" + ex.Message);
                }
            }
            foreach (var tool in etl.CurrentETLTools.Skip(start).Take(end - start))
            {
                yield return tool;
            }
        }

 
    }

    [XFrmWork("子任务-生成", "从其他数据清洗模块中生成序列，用以组合大模块")]
    public class EtlGE : ETLBase, IColumnGenerator
    {


        [LocalizedDisplayName("生成模式")]
        public MergeType MergeType { get; set; }

        public IEnumerable<FreeDocument> Generate(IFreeDocument document = null)
        {
            var process = GetProcesses().ToList();
            if (process.Any() == false)
            {
                yield break;
            }
            foreach (var item in etl.Generate(process, IsExecute).Select(d => d as FreeDocument))
            {
                yield return item;
            }
        }
        public int? GenerateCount()
        {
            return null;
        }

        public override FreeDocument DictSerialize(Scenario scenario = Scenario.Database)
        {
            var data = base.DictSerialize(scenario);
            data.SetValue("Group", "Generator");
            return data;
        }
    }


    [XFrmWork("子任务-执行", "从其他数据清洗模块中生成序列，用以组合大模块")]
    public class EtlEX : ETLBase, IDataExecutor
    {
        private EnumerableFunc func;

        [LocalizedDisplayName("添加到任务")]
        [LocalizedDescription("勾选后，本子任务会添加到任务管理器中")]
        public bool AddTask { get; set; }


          [LocalizedCategory("1.基本选项")]
        [LocalizedDisplayName("输出列")]
        [LocalizedDescription("从原始数据中传递到子执行流的列，多个列用空格分割")]
        public string NewColumn { get; set; }

        public override bool Init(IEnumerable<IFreeDocument> datas)
        {
            base.Init(datas);
            var process = GetProcesses().ToList();
            func = etl.Aggregate(d => d, process, true);
            return true;
        }

        public override FreeDocument DictSerialize(Scenario scenario = Scenario.Database)
        {
            var data = base.DictSerialize(scenario);
            data.SetValue("Group", "Executor");
            return data;
        }

        public IEnumerable<IFreeDocument> Execute(IEnumerable<IFreeDocument> documents)
        {
            foreach (var document in documents)
            {
                IFreeDocument doc = null;
                if (string.IsNullOrEmpty(NewColumn))
                {
                    doc = document.Clone();
                }
                else
                {
                    doc = new FreeDocument();
                    doc.MergeQuery(document, NewColumn + " " + Column);
                }
                if (AddTask)
                {
                    var name = doc[Column];
                    ControlExtended.UIInvoke(() =>
                    {
                        var task = TemporaryTask.AddTempTask("ETL" + name, func(new List<IFreeDocument> { doc }),
                            d => d.ToList());
                        processManager.CurrentProcessTasks.Add(task);
                    });
                }
                else
                {
                    var r = func(new List<IFreeDocument> { doc }).ToList();
                }

                yield return document;
            }
        }
    }

    [XFrmWork("矩阵转置", "将列数据转换为行数据，拖入的列为key")]
    public class DictTF : TransformerBase
    {

        public override bool Init(IEnumerable<IFreeDocument> docus)
        {
            IsMultiYield = true;
            return base.Init(docus);
        }

        public override IEnumerable<IFreeDocument> TransformManyData(IEnumerable<IFreeDocument> datas)
        {

            if (string.IsNullOrEmpty(Column))
            {

                foreach (var data in datas)
                {
                    yield return data;
                }
                yield break;

            }
            var hasyield = false;
            var results = datas.ToList();
            var columns = results.Select(d => d[Column].ToString()).ToList();
            var all_keys = results.GetKeys(count: 100).ToList();
            var docs = new List<FreeDocument>();
            for (int i = 0; i < all_keys.Count(); i++)
            {
                docs.Add(new FreeDocument());
            }
            int pos = 0;
            foreach (var column in columns)
            {
                int pos2 = 0;
                foreach (var doc in docs)
                {
                    doc[column] = results[pos][all_keys[pos2++]];
                }
                pos += 1;
            }
            foreach (var doc in docs)
            {
                yield return doc;
            }
            //数字列可能会有不显示的问题
        }
    }

    [XFrmWork("子任务-转换", "调用所选的子任务作为转换器，有关子任务，请参考相关文档")]
    public class EtlTF : ETLBase, IColumnDataTransformer
    {
        private EnumerableFunc func;
        private IEnumerable<IColumnProcess> process;

        public EtlTF()
        {
            NewColumn = "";
            IsCycle = false;
        }
        [LocalizedDisplayName("递归到下列")]
        public bool IsCycle { get; set; }

        [LocalizedCategory("1.基本选项")]
        [LocalizedDisplayName("输出列")]
        [LocalizedDescription("从原任务中传递到子任务的列，多个列用空格分割")]
        public string NewColumn { get; set; }

        [Browsable(false)]
        public bool OneOutput => false;

        public override bool Init(IEnumerable<IFreeDocument> datas)
        {
            base.Init(datas);
            process = GetProcesses();
            func = etl.Aggregate(d => d, process, IsExecute);
            return true;
        }

        public object TransformData(IFreeDocument data)
        {
            var result = func(new List<IFreeDocument> { data.Clone() }).FirstOrDefault();
            data.AddRange(result);
            return null;
        }

        [LocalizedDisplayName("返回多个数据")]
        public bool IsMultiYield { get; set; }

        public IEnumerable<IFreeDocument> TransformManyData(IEnumerable<IFreeDocument> datas)
        {
            foreach (var data in datas)
            {
                if (IsCycle)
                {
                    var newdata = data;
                    while (string.IsNullOrEmpty(newdata[Column].ToString()) == false)
                    {
                        var result =
                            etl.Generate(process, IsExecute, new List<IFreeDocument> { newdata.Clone() }).FirstOrDefault();
                        if (result == null)
                            break;
                        yield return result.Clone();
                        newdata = result;
                    }
                }
                else
                {
                    var result = etl.Generate(process, IsExecute, new List<IFreeDocument> { data.Clone() });
                    foreach (var item in result)
                    {
                        yield return item.MergeQuery(data, NewColumn);
                    }
                }
            }
        }

        public override FreeDocument DictSerialize(Scenario scenario = Scenario.Database)
        {
            var data = base.DictSerialize(scenario);
            data.SetValue("Group", "Transformer");
            return data;
        }
    }
}