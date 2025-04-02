//----------------------
// <auto-generated>
//     Generated using the NJsonSchema v10.9.0.0 (Newtonsoft.Json v13.0.0.0) (http://NJsonSchema.org)
// </auto-generated>
//----------------------


namespace AindPhysiologyFip.TaskLogic
{
    #pragma warning disable // Disable all warnings

    [System.CodeDom.Compiler.GeneratedCodeAttribute("Bonsai.Sgen", "0.4.0.0 (Newtonsoft.Json v13.0.0.0)")]
    [Bonsai.CombinatorAttribute()]
    [Bonsai.WorkflowElementCategoryAttribute(Bonsai.ElementCategory.Source)]
    public partial class AindPhysioFipParameters
    {
    
        private double? _rngSeed;
    
        private string _aindBehaviorServicesPkgVersion = "0.10.2";
    
        public AindPhysioFipParameters()
        {
        }
    
        protected AindPhysioFipParameters(AindPhysioFipParameters other)
        {
            _rngSeed = other._rngSeed;
            _aindBehaviorServicesPkgVersion = other._aindBehaviorServicesPkgVersion;
        }
    
        /// <summary>
        /// Seed of the random number generator
        /// </summary>
        [System.Xml.Serialization.XmlIgnoreAttribute()]
        [Newtonsoft.Json.JsonPropertyAttribute("rng_seed")]
        [System.ComponentModel.DescriptionAttribute("Seed of the random number generator")]
        public double? RngSeed
        {
            get
            {
                return _rngSeed;
            }
            set
            {
                _rngSeed = value;
            }
        }
    
        [Newtonsoft.Json.JsonPropertyAttribute("aind_behavior_services_pkg_version")]
        public string AindBehaviorServicesPkgVersion
        {
            get
            {
                return _aindBehaviorServicesPkgVersion;
            }
            set
            {
                _aindBehaviorServicesPkgVersion = value;
            }
        }
    
        public System.IObservable<AindPhysioFipParameters> Process()
        {
            return System.Reactive.Linq.Observable.Defer(() => System.Reactive.Linq.Observable.Return(new AindPhysioFipParameters(this)));
        }
    
        public System.IObservable<AindPhysioFipParameters> Process<TSource>(System.IObservable<TSource> source)
        {
            return System.Reactive.Linq.Observable.Select(source, _ => new AindPhysioFipParameters(this));
        }
    
        protected virtual bool PrintMembers(System.Text.StringBuilder stringBuilder)
        {
            stringBuilder.Append("rng_seed = " + _rngSeed + ", ");
            stringBuilder.Append("aind_behavior_services_pkg_version = " + _aindBehaviorServicesPkgVersion);
            return true;
        }
    
        public override string ToString()
        {
            System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
            stringBuilder.Append(GetType().Name);
            stringBuilder.Append(" { ");
            if (PrintMembers(stringBuilder))
            {
                stringBuilder.Append(" ");
            }
            stringBuilder.Append("}");
            return stringBuilder.ToString();
        }
    }


    /// <summary>
    /// Olfactometer operation control model that is used to run a calibration data acquisition workflow
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Bonsai.Sgen", "0.4.0.0 (Newtonsoft.Json v13.0.0.0)")]
    [System.ComponentModel.DescriptionAttribute("Olfactometer operation control model that is used to run a calibration data acqui" +
        "sition workflow")]
    [Bonsai.CombinatorAttribute()]
    [Bonsai.WorkflowElementCategoryAttribute(Bonsai.ElementCategory.Source)]
    public partial class AindPhysioFipTaskLogic
    {
    
        private string _name = "AindPhysiologyFip";
    
        private string _description = "";
    
        private AindPhysioFipParameters _taskParameters;
    
        private string _version = "0.1.0";
    
        private string _stageName;
    
        public AindPhysioFipTaskLogic()
        {
        }
    
        protected AindPhysioFipTaskLogic(AindPhysioFipTaskLogic other)
        {
            _name = other._name;
            _description = other._description;
            _taskParameters = other._taskParameters;
            _version = other._version;
            _stageName = other._stageName;
        }
    
        [Newtonsoft.Json.JsonPropertyAttribute("name")]
        public string Name
        {
            get
            {
                return _name;
            }
            set
            {
                _name = value;
            }
        }
    
        /// <summary>
        /// Description of the task.
        /// </summary>
        [Newtonsoft.Json.JsonPropertyAttribute("description")]
        [System.ComponentModel.DescriptionAttribute("Description of the task.")]
        public string Description
        {
            get
            {
                return _description;
            }
            set
            {
                _description = value;
            }
        }
    
        [System.Xml.Serialization.XmlIgnoreAttribute()]
        [Newtonsoft.Json.JsonPropertyAttribute("task_parameters")]
        public AindPhysioFipParameters TaskParameters
        {
            get
            {
                return _taskParameters;
            }
            set
            {
                _taskParameters = value;
            }
        }
    
        [Newtonsoft.Json.JsonPropertyAttribute("version")]
        public string Version
        {
            get
            {
                return _version;
            }
            set
            {
                _version = value;
            }
        }
    
        /// <summary>
        /// Optional stage name the `Task` object instance represents.
        /// </summary>
        [Newtonsoft.Json.JsonPropertyAttribute("stage_name")]
        [System.ComponentModel.DescriptionAttribute("Optional stage name the `Task` object instance represents.")]
        public string StageName
        {
            get
            {
                return _stageName;
            }
            set
            {
                _stageName = value;
            }
        }
    
        public System.IObservable<AindPhysioFipTaskLogic> Process()
        {
            return System.Reactive.Linq.Observable.Defer(() => System.Reactive.Linq.Observable.Return(new AindPhysioFipTaskLogic(this)));
        }
    
        public System.IObservable<AindPhysioFipTaskLogic> Process<TSource>(System.IObservable<TSource> source)
        {
            return System.Reactive.Linq.Observable.Select(source, _ => new AindPhysioFipTaskLogic(this));
        }
    
        protected virtual bool PrintMembers(System.Text.StringBuilder stringBuilder)
        {
            stringBuilder.Append("name = " + _name + ", ");
            stringBuilder.Append("description = " + _description + ", ");
            stringBuilder.Append("task_parameters = " + _taskParameters + ", ");
            stringBuilder.Append("version = " + _version + ", ");
            stringBuilder.Append("stage_name = " + _stageName);
            return true;
        }
    
        public override string ToString()
        {
            System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
            stringBuilder.Append(GetType().Name);
            stringBuilder.Append(" { ");
            if (PrintMembers(stringBuilder))
            {
                stringBuilder.Append(" ");
            }
            stringBuilder.Append("}");
            return stringBuilder.ToString();
        }
    }


    /// <summary>
    /// Serializes a sequence of data model objects into JSON strings.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Bonsai.Sgen", "0.4.0.0 (Newtonsoft.Json v13.0.0.0)")]
    [System.ComponentModel.DescriptionAttribute("Serializes a sequence of data model objects into JSON strings.")]
    [Bonsai.CombinatorAttribute()]
    [Bonsai.WorkflowElementCategoryAttribute(Bonsai.ElementCategory.Transform)]
    public partial class SerializeToJson
    {
    
        private System.IObservable<string> Process<T>(System.IObservable<T> source)
        {
            return System.Reactive.Linq.Observable.Select(source, value => Newtonsoft.Json.JsonConvert.SerializeObject(value));
        }

        public System.IObservable<string> Process(System.IObservable<AindPhysioFipParameters> source)
        {
            return Process<AindPhysioFipParameters>(source);
        }

        public System.IObservable<string> Process(System.IObservable<AindPhysioFipTaskLogic> source)
        {
            return Process<AindPhysioFipTaskLogic>(source);
        }
    }


    /// <summary>
    /// Deserializes a sequence of JSON strings into data model objects.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Bonsai.Sgen", "0.4.0.0 (Newtonsoft.Json v13.0.0.0)")]
    [System.ComponentModel.DescriptionAttribute("Deserializes a sequence of JSON strings into data model objects.")]
    [System.ComponentModel.DefaultPropertyAttribute("Type")]
    [Bonsai.WorkflowElementCategoryAttribute(Bonsai.ElementCategory.Transform)]
    [System.Xml.Serialization.XmlIncludeAttribute(typeof(Bonsai.Expressions.TypeMapping<AindPhysioFipParameters>))]
    [System.Xml.Serialization.XmlIncludeAttribute(typeof(Bonsai.Expressions.TypeMapping<AindPhysioFipTaskLogic>))]
    public partial class DeserializeFromJson : Bonsai.Expressions.SingleArgumentExpressionBuilder
    {
    
        public DeserializeFromJson()
        {
            Type = new Bonsai.Expressions.TypeMapping<AindPhysioFipTaskLogic>();
        }

        public Bonsai.Expressions.TypeMapping Type { get; set; }

        public override System.Linq.Expressions.Expression Build(System.Collections.Generic.IEnumerable<System.Linq.Expressions.Expression> arguments)
        {
            var typeMapping = (Bonsai.Expressions.TypeMapping)Type;
            var returnType = typeMapping.GetType().GetGenericArguments()[0];
            return System.Linq.Expressions.Expression.Call(
                typeof(DeserializeFromJson),
                "Process",
                new System.Type[] { returnType },
                System.Linq.Enumerable.Single(arguments));
        }

        private static System.IObservable<T> Process<T>(System.IObservable<string> source)
        {
            return System.Reactive.Linq.Observable.Select(source, value => Newtonsoft.Json.JsonConvert.DeserializeObject<T>(value));
        }
    }
}