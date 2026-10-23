#if UNITY_EDITOR
using System.Linq;
using ShockWave.ToyControl.Shared;
using UnityEditor.Animations;
using UnityEngine;

namespace ShockWave.ToyControl
{
    public sealed partial class ToyControlSetupWindow
    {
        private static bool PlacementAcceptsInput(string name) => name=="Dropped" || name=="Follow right hand" || name=="Follow back of right hand" || name=="Adjusted in right hand" || name=="Adjusted back of right hand" || name=="Grab left hand" || name=="Grab right hand";

        private static void PlacementLayer(AnimatorController c)
        {
            var place=Layer(c,"Local placement");
            var hidden=State(place,"Hidden",Placement(c,"Hidden",false,false,.2f,contacts:false));place.defaultState=hidden;
            var spawn=State(place,"Place beside right hand",Placement(c,"Place",true,false,.2f,contacts:false));
            var world=State(place,"Dropped",Placement(c,"Dropped",true,true,.1f));
            var presetIn=State(place,"Follow right hand",Placement(c,"Follow",true,false,.1f));
            var presetBack=State(place,"Follow back of right hand",Placement(c,"Back hand",true,false,.1f,3));
            var fitIn=State(place,"Adjusted in right hand",Placement(c,"Adjusted in hand",true,false,.1f,4));
            var fitBack=State(place,"Adjusted back of right hand",Placement(c,"Adjusted back hand",true,false,.1f,5));
            var prepareLeft=State(place,"Prepare left grab",Placement(c,"Prepare left grab",true,true,.05f,1,true,contacts:false));
            var prepareRight=State(place,"Prepare right grab",Placement(c,"Prepare right grab",true,true,.05f,2,true,contacts:false));
            var grabLeft=State(place,"Grab left hand",Placement(c,"Grab left hand",true,false,.1f,1,true));
            var grabRight=State(place,"Grab right hand",Placement(c,"Grab right hand",true,false,.1f,2,true));
            var release=State(place,"Release touch guard",Placement(c,"Release touch guard",true,true,.2f,contacts:false));
            var prepareIn=State(place,"Prepare in-hand fit",Placement(c,"Prepare in-hand fit",true,true,.05f,4,true,contacts:false));
            var prepareBack=State(place,"Prepare back-hand fit",Placement(c,"Prepare back-hand fit",true,true,.05f,5,true,contacts:false));
            var bakeIn=State(place,"Capture in-hand fit",Placement(c,"Capture in-hand fit",true,false,.05f,4,true,contacts:false));
            var bakeBack=State(place,"Capture back-hand fit",Placement(c,"Capture back-hand fit",true,false,.05f,5,true,contacts:false));
            var recall=State(place,"Recall beside right hand",Placement(c,"Recall",true,false,.2f,contacts:false));
            Driver(bakeIn,"FitInHand",1);Driver(bakeBack,"FitBackHand",1);
            // The SDK rebakes every source offset, including zero-weight ones.
            // A new grip therefore replaces the previous temporary fit.
            foreach(var state in new[]{prepareLeft,prepareRight}) {Driver(state,"FitInHand",0);Driver(state,"FitBackHand",0);}
            Driver(recall,"FitInHand",0);Driver(recall,"FitBackHand",0);Driver(recall,"HandMode",0);

            void Mode(AnimatorStateTransition t,int value) => t.AddCondition(AnimatorConditionMode.Equals,value,ToyControlContract.Prefix+"HandMode");
            void Timed(AnimatorStateTransition t) {t.hasExitTime=true;t.exitTime=1;}
            void Presets(AnimatorState state,bool timed=false)
            {
                foreach(var target in new[]{(presetIn,1,false,"FitInHand"),(fitIn,1,true,"FitInHand"),(presetBack,2,false,"FitBackHand"),(fitBack,2,true,"FitBackHand")})
                {
                    if(state==target.Item1) continue;
                    var t=Transition(state,target.Item1);Mode(t,target.Item2);Bool(t,target.Item4,target.Item3);Bool(t,"Recall",false);if(timed) Timed(t);
                }
            }
            var show=Transition(hidden,spawn);Bool(show,"Visible",true);show.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            foreach(var state in place.states.Select(s=>s.state).Where(s=>s!=hidden))
            {Bool(Transition(state,hidden),"Visible",false);Transition(state,hidden).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");}
            foreach(var state in new[]{world,presetIn,presetBack,fitIn,fitBack})
            {
                Presets(state);
                if(state!=world) Mode(Transition(state,release),0);
                Bool(Transition(state,recall),"Recall",true);
                foreach(var side in new[]{"Left","Right"}) foreach(var gesture in GrabGestures)
                {
                    var left=Transition(state,prepareLeft);Bool(left,"GrabL"+side,true);left.AddCondition(AnimatorConditionMode.Equals,gesture,"GestureLeft");
                    // The mounted right wrist cannot grab its own handle. The free
                    // left hand may refit it; a floating panel accepts either hand.
                    if(state==world)
                    {
                        Mode(left,0);var right=Transition(state,prepareRight);Mode(right,0);Bool(right,"GrabR"+side,true);right.AddCondition(AnimatorConditionMode.Equals,gesture,"GestureRight");
                    }
                }
            }
            foreach(var pair in new[]{(prepareLeft,grabLeft,"GestureLeft"),(prepareRight,grabRight,"GestureRight")})
            {
                var attach=Transition(pair.Item1,pair.Item2);Timed(attach);attach.AddCondition(AnimatorConditionMode.Equals,1,pair.Item3);
                foreach(var state in new[]{pair.Item1,pair.Item2}) Transition(state,release).AddCondition(AnimatorConditionMode.NotEqual,1,pair.Item3);
                Bool(Transition(pair.Item2,recall),"Recall",true);
            }
            Presets(spawn,true);var drop=Transition(spawn,world);Timed(drop);Mode(drop,0);Bool(drop,"Recall",false);
            var dropped=Transition(release,world);Timed(dropped);Mode(dropped,0);foreach(var grip in GripInputs) Bool(dropped,grip,false);
            foreach(var target in new[]{(prepareIn,1),(prepareBack,2)}) {var t=Transition(release,target.Item1);Timed(t);Mode(t,target.Item2);}
            Bool(Transition(release,recall),"Recall",true);
            foreach(var pair in new[]{(prepareIn,bakeIn,fitIn),(prepareBack,bakeBack,fitBack)})
            {Timed(Transition(pair.Item1,pair.Item2));Timed(Transition(pair.Item2,pair.Item3));}
            var recalled=Transition(recall,world);Timed(recalled);Bool(recalled,"Recall",false);
            foreach(var state in place.states.Select(s=>s.state))
            {
                var held=state==grabLeft ? 1 : state==grabRight || state==presetIn || state==presetBack || state==fitIn || state==fitBack ? 2 : 0;
                Driver(state,"HoldingHand",held);
                if(!PlacementAcceptsInput(state.name))
                    foreach(var name in new[]{"InTouch","InPanelTouch","InPage","InTestTouch","InRecord","InPlay","InHold","InClear","InStop","InOwn","InRemote","InBoth"}) Driver(state,name,0);
            }
        }
    }
}
#endif
