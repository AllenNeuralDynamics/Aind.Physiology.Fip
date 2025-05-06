using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;

namespace FipExtensions
{
    [Combinator]
    [Description("Returns an Mat containing buffered CircleActivityCollection.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class CircleActivityToMat
    {
        private int bufferSize = 5;

        private int skipSize = 5;

        [Description("The number of frames in each buffer.")]
        public int BufferSize
        {
            get { return bufferSize; }
            set { bufferSize = value; }
        }


        [Description("The number of frames in each buffer.")]
        public int SkipSize
        {
            get { return skipSize; }
            set { skipSize = value; }
        }

        public IObservable<Mat> Process(IObservable<CircleActivityCollection> source)
        {
            return source.Buffer(BufferSize, SkipSize).Select(value =>
            {
                var nRegions = value.Select(x => x.Count);

                if (!nRegions.All(x => x > 0)) { throw new ArgumentException("One or more collections have no regions."); }
                if (nRegions.Distinct().Count() != 1) { throw new ArgumentException("Not all collections have the same number of regions."); }

                var nCh = value[0].Count;
                var src = new double[nCh * BufferSize];
                for (int i = 0; i < BufferSize; i++)
                {
                    for (int j = 0; j < nCh; j++)
                    {
                        src[j * BufferSize + i] = value[i][j].Activity.Val0;
                    }
                }
                Mat mat = Mat.FromArray(src, nCh, BufferSize, Depth.F64, 1);

                return mat;
            });
        }
    }
}